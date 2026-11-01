using PosTech.Contratos;

namespace PosTech.Mensageria;

/// <summary>
/// Acesso à tabela de outbox. Cada serviço implementa com o seu próprio
/// repositório, Dapper ou Mongo, no banco dele.
/// </summary>
public interface IRepositorioDeOutbox
{
    /// <summary>Pendentes cuja hora de tentar já chegou, mais antigos primeiro.</summary>
    Task<IReadOnlyList<EventoOutbox>> ObterPendentesAsync(int limite, CancellationToken cancellationToken = default);

    /// <summary>
    /// A identidade do evento no outbox é a <c>MensagemId</c>, e não a chave da
    /// tabela. O outbox SQL tem um Id numérico, o do Mongo vive embutido num
    /// documento e não tem; a MensagemId existe nos dois e é a mesma que viaja
    /// no envelope, então é por ela que o processador fala com os dois.
    /// </summary>
    Task MarcarPublicadoAsync(Guid mensagemId, DateTime publicadoEm, CancellationToken cancellationToken = default);

    Task RegistrarFalhaAsync(
        Guid mensagemId, int tentativas, DateTime proximaTentativaEm, string erro, EStatusOutbox status,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converte um evento de domínio em linha de outbox.
///
/// É aqui que o caso de uso encosta na mensageria: ele chama isto e grava o
/// resultado na mesma transação do negócio. Não existe publicação direta em
/// caso de uso nenhum, e é essa regra que faz o padrão valer.
/// </summary>
public static class Outbox
{
    public static EventoOutbox Enfileirar<T>(T evento, string correlationId, string origem, DateTime? agora = null)
        where T : notnull
    {
        var envelope = Envelope<T>.Criar(evento, correlationId, origem);
        var atributo = EventoDeIntegracaoAttribute.De(typeof(T));
        var instante = agora ?? DateTime.UtcNow;

        return new EventoOutbox
        {
            MensagemId = envelope.MensagemId,
            Tipo = envelope.Tipo,
            RoutingKey = atributo.RoutingKey,
            Payload = SerializacaoDeEvento.Serializar(envelope),
            CorrelationId = correlationId,
            Status = EStatusOutbox.Pendente,
            Tentativas = 0,
            CriadoEm = instante,
            ProximaTentativaEm = instante,
        };
    }
}

/// <summary>
/// Lê o outbox e entrega ao broker.
///
/// A ordem das operações é o ponto: publica, espera a confirmação, e só então
/// marca como publicado. Se o processo morrer entre a confirmação e a marcação,
/// o evento é republicado na próxima volta — o consumidor descarta pela
/// MensagemId. Entregar duas vezes é recuperável; perder não é.
/// </summary>
public sealed class ProcessadorDoOutbox
{
    public const int MaximoDeTentativas = 8;

    private readonly IRepositorioDeOutbox _repositorio;
    private readonly IPublicadorDeEventos _publicador;
    private readonly Func<DateTime> _agora;

    public ProcessadorDoOutbox(
        IRepositorioDeOutbox repositorio,
        IPublicadorDeEventos publicador,
        Func<DateTime>? agora = null)
    {
        _repositorio = repositorio ?? throw new ArgumentNullException(nameof(repositorio));
        _publicador = publicador ?? throw new ArgumentNullException(nameof(publicador));
        _agora = agora ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Processa um lote. Devolve quantos foram publicados com sucesso.
    /// Uma falha isolada não derruba o lote: o evento seguinte ainda é tentado.
    /// </summary>
    public async Task<int> ProcessarLoteAsync(int limite = 50, CancellationToken cancellationToken = default)
    {
        var pendentes = await _repositorio.ObterPendentesAsync(limite, cancellationToken).ConfigureAwait(false);
        var publicados = 0;

        foreach (var evento in pendentes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _publicador.PublicarAsync(evento, cancellationToken).ConfigureAwait(false);
                await _repositorio.MarcarPublicadoAsync(evento.MensagemId, _agora(), cancellationToken).ConfigureAwait(false);
                publicados++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception excecao)
            {
                var tentativas = evento.Tentativas + 1;
                var status = tentativas >= MaximoDeTentativas ? EStatusOutbox.Falhou : EStatusOutbox.Pendente;

                await _repositorio.RegistrarFalhaAsync(
                    evento.MensagemId,
                    tentativas,
                    _agora().Add(Backoff(tentativas)),
                    Resumir(excecao),
                    status,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return publicados;
    }

    /// <summary>
    /// Backoff exponencial com teto de cinco minutos.
    ///
    /// Sem backoff, um broker fora do ar vira um laço apertado que só gasta CPU
    /// e enche o log. O teto existe porque a próxima tentativa precisa acontecer
    /// em tempo humano quando o broker volta.
    /// </summary>
    public static TimeSpan Backoff(int tentativas)
    {
        if (tentativas < 1)
            tentativas = 1;

        var segundos = Math.Pow(2, Math.Min(tentativas, 10));

        return TimeSpan.FromSeconds(Math.Min(segundos, 300));
    }

    private static string Resumir(Exception excecao)
    {
        var texto = $"{excecao.GetType().Name}: {excecao.Message}";

        // A coluna é NVARCHAR(2000); truncar aqui evita que o registro da falha
        // vire uma segunda falha.
        return texto.Length <= 2000 ? texto : texto[..2000];
    }
}
