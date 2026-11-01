using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using PosTech.Mensageria;

namespace PosTech.Billing;

/// <summary>
/// Um evento que este serviço já processou.
///
/// O <c>_id</c> é a própria chave de negócio — mensagem + consumidor —, e não
/// um id gerado. Assim a unicidade vem do índice que o Mongo cria sozinho para
/// o <c>_id</c>: não há índice extra para criar nem como esquecer de criá-lo.
/// </summary>
public sealed class EventoProcessado
{
    [BsonId]
    public string Id { get; set; } = string.Empty;

    [BsonRepresentation(BsonType.String)]
    public Guid MensagemId { get; set; }

    public string Consumidor { get; set; } = string.Empty;

    public string Tipo { get; set; } = string.Empty;

    public DateTime ProcessadoEm { get; set; }

    public static string ChaveDe(Guid mensagemId, string consumidor) => $"{mensagemId:D}:{consumidor}";
}

/// <summary>
/// Registro de idempotência em MongoDB.
///
/// Mesmo papel do <c>EventoProcessado</c> em Azure SQL do OS Service: o
/// RabbitMQ entrega ao menos uma vez, e sem este registro a segunda entrega de
/// um <c>DiagnosticoConcluido</c> viraria um segundo orçamento.
/// </summary>
public sealed class IdempotenciaMongo : IRegistroDeIdempotencia
{
    /// <summary>
    /// Quanto tempo o registro fica. Trinta dias é muito além de qualquer
    /// janela de reentrega do broker, e evita que a coleção cresça para sempre
    /// — o equivalente da tabela SQL não tem essa limpeza e um dia vai doer.
    /// </summary>
    public static readonly TimeSpan Retencao = TimeSpan.FromDays(30);

    private readonly IMongoCollection<EventoProcessado> _colecao;

    public IdempotenciaMongo(IMongoDatabase banco)
    {
        ArgumentNullException.ThrowIfNull(banco);

        _colecao = banco.GetCollection<EventoProcessado>("eventosProcessados");
    }

    public async Task GarantirIndicesAsync(CancellationToken cancellationToken = default)
    {
        await _colecao.Indexes.CreateOneAsync(
            new CreateIndexModel<EventoProcessado>(
                Builders<EventoProcessado>.IndexKeys.Ascending(e => e.ProcessadoEm),
                new CreateIndexOptions { Name = "ttl_processadoEm", ExpireAfter = Retencao }),
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> JaProcessadoAsync(Guid mensagemId, string consumidor, CancellationToken cancellationToken = default)
    {
        var chave = EventoProcessado.ChaveDe(mensagemId, consumidor);

        return await _colecao.Find(e => e.Id == chave)
            .AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RegistrarAsync(
        Guid mensagemId, string tipo, string consumidor, DateTime quando, CancellationToken cancellationToken = default)
    {
        var registro = new EventoProcessado
        {
            Id = EventoProcessado.ChaveDe(mensagemId, consumidor),
            MensagemId = mensagemId,
            Consumidor = consumidor,
            Tipo = tipo,
            ProcessadoEm = quando,
        };

        try
        {
            await _colecao.ReplaceOneAsync(
                e => e.Id == registro.Id,
                registro,
                new ReplaceOptions { IsUpsert = true },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MongoWriteException excecao) when (excecao.WriteError?.Category == ServerErrorCategory.DuplicateKey)
        {
            // Duas réplicas podem passar pela checagem juntas e tentar gravar
            // as duas. Deixar a violação subir faria o consumidor mandar para a
            // dead letter uma mensagem que foi processada com sucesso — o pior
            // resultado possível. Mesma decisão do IF NOT EXISTS no SQL.
        }
    }
}
