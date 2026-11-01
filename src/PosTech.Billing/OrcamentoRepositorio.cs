using MongoDB.Driver;
using PosTech.Mensageria;

namespace PosTech.Billing;

public interface IOrcamentoRepositorio
{
    Task<Orcamento?> ObterPorOrdemAsync(int idOrdem, CancellationToken cancellationToken = default);

    Task<Orcamento?> ObterAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Pelo id da cobrança no adquirente. É assim que o webhook descobre de
    /// qual orçamento a notificação está falando.
    /// </summary>
    Task<Orcamento?> ObterPorReferenciaExternaAsync(string referencia, CancellationToken cancellationToken = default);

    /// <summary>
    /// Grava o documento inteiro, eventos pendentes incluídos. Uma escrita só,
    /// atômica por ser de um documento — é assim que o outbox funciona aqui.
    /// </summary>
    Task SalvarAsync(Orcamento orcamento, CancellationToken cancellationToken = default);
}

public sealed class OrcamentoRepositorio : IOrcamentoRepositorio
{
    private readonly IMongoCollection<Orcamento> _colecao;

    public OrcamentoRepositorio(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<Orcamento>("orcamentos");
    }

    /// <summary>
    /// Índices. Idempotente, como o schema SQL: rodar na subida do serviço é
    /// seguro.
    ///
    /// O índice em IdOrdem é único porque uma ordem tem no máximo um orçamento,
    /// e deixar isso só na disciplina da aplicação é como a Fase 3 ficou sem
    /// UNIQUE em CPF.
    /// </summary>
    public async Task GarantirIndicesAsync(CancellationToken cancellationToken = default)
    {
        await _colecao.Indexes.CreateManyAsync(
            [
                new CreateIndexModel<Orcamento>(
                    Builders<Orcamento>.IndexKeys.Ascending(o => o.IdOrdem),
                    new CreateIndexOptions { Unique = true, Name = "ux_idOrdem" }),

                // O publicador pergunta sempre a mesma coisa: quem tem evento
                // pendente. Sem índice, isso varre a coleção a cada volta.
                new CreateIndexModel<Orcamento>(
                    Builders<Orcamento>.IndexKeys.Ascending("EventosPendentes.ProximaTentativaEm"),
                    new CreateIndexOptions { Name = "ix_eventosPendentes", Sparse = true }),

                // O webhook chega com o id do adquirente e precisa achar o
                // orçamento. Sem índice, cada notificação varre a coleção.
                new CreateIndexModel<Orcamento>(
                    Builders<Orcamento>.IndexKeys.Ascending(o => o.ReferenciaExterna),
                    new CreateIndexOptions { Name = "ix_referenciaExterna", Sparse = true }),
            ],
            cancellationToken);
    }

    public async Task<Orcamento?> ObterPorOrdemAsync(int idOrdem, CancellationToken cancellationToken = default) =>
        await _colecao.Find(o => o.IdOrdem == idOrdem).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Orcamento?> ObterAsync(string id, CancellationToken cancellationToken = default) =>
        await _colecao.Find(o => o.Id == id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task<Orcamento?> ObterPorReferenciaExternaAsync(string referencia, CancellationToken cancellationToken = default) =>
        await _colecao.Find(o => o.ReferenciaExterna == referencia).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task SalvarAsync(Orcamento orcamento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        await _colecao.ReplaceOneAsync(
            o => o.Id == orcamento.Id,
            orcamento,
            new ReplaceOptions { IsUpsert = true },
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Outbox do Billing: lê e remove os eventos embutidos nos orçamentos.
///
/// Implementa a mesma interface do outbox SQL, então o
/// <see cref="ProcessadorDoOutbox"/> funciona igual nos dois serviços sem saber
/// onde o dado mora.
/// </summary>
public sealed class OutboxMongo : IRepositorioDeOutbox
{
    private readonly IMongoCollection<Orcamento> _colecao;

    public OutboxMongo(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<Orcamento>("orcamentos");
    }

    public async Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default)
    {
        var agora = DateTime.UtcNow;

        var filtro = Builders<Orcamento>.Filter.ElemMatch(
            o => o.EventosPendentes,
            e => e.Status == EStatusOutbox.Pendente && e.ProximaTentativaEm <= agora);

        var documentos = await _colecao.Find(filtro).Limit(limite).ToListAsync(cancellationToken).ConfigureAwait(false);

        return documentos
            .SelectMany(d => d.EventosPendentes)
            .Where(e => e.Status == EStatusOutbox.Pendente && e.ProximaTentativaEm <= agora)
            .OrderBy(e => e.CriadoEm)
            .Take(limite)
            .ToList();
    }

    /// <summary>
    /// Publicado sai do documento. Diferente do SQL, onde a linha fica marcada,
    /// aqui o evento é removido — manter histórico de publicação dentro do
    /// agregado faria o documento crescer sem teto.
    /// </summary>
    public async Task MarcarPublicadoAsync(Guid mensagemId, DateTime publicadoEm, CancellationToken cancellationToken = default)
    {
        var filtro = Builders<Orcamento>.Filter.ElemMatch(o => o.EventosPendentes, e => e.MensagemId == mensagemId);
        var remocao = Builders<Orcamento>.Update.PullFilter(o => o.EventosPendentes, e => e.MensagemId == mensagemId);

        await _colecao.UpdateOneAsync(filtro, remocao, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task RegistrarFalhaAsync(
        Guid mensagemId, int tentativas, DateTime proximaTentativaEm, string erro, EStatusOutbox status,
        CancellationToken cancellationToken = default)
    {
        var filtro = Builders<Orcamento>.Filter.ElemMatch(o => o.EventosPendentes, e => e.MensagemId == mensagemId);

        var atualizacao = Builders<Orcamento>.Update
            .Set("EventosPendentes.$.Tentativas", tentativas)
            .Set("EventosPendentes.$.ProximaTentativaEm", proximaTentativaEm)
            .Set("EventosPendentes.$.UltimoErro", erro)
            .Set("EventosPendentes.$.Status", status);

        await _colecao.UpdateOneAsync(filtro, atualizacao, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
