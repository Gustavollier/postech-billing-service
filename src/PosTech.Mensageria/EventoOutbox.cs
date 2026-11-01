namespace PosTech.Mensageria;

/// <summary>
/// Evento aguardando publicação, gravado na mesma transação do caso de uso.
///
/// É a generalização do <c>EmailOutbox</c> da Fase 3, que já fazia metade disto:
/// gravava a mensagem junto com a regra de negócio. A outra metade, o processo
/// que lê e entrega, nunca existiu — e é ela que a saga precisa.
///
/// O problema que o outbox resolve: gravar a ordem no banco e publicar no broker
/// são dois recursos diferentes e não cabem numa transação só. Publicar antes de
/// gravar inventa um evento sobre uma ordem que pode não existir; gravar antes de
/// publicar perde o evento se o processo morrer no meio. Com outbox, gravar a
/// ordem e enfileirar o evento é uma transação local única, e um processo à parte
/// entrega depois. Nada se perde, e nada é inventado.
/// </summary>
public sealed class EventoOutbox
{
    public long Id { get; set; }

    /// <summary>
    /// O mesmo id que vai no envelope. É o que permite ao consumidor descartar
    /// a entrega repetida, e ao publicador reenviar sem medo.
    /// </summary>
    public Guid MensagemId { get; set; }

    /// <summary>Nome público do evento, de <c>[EventoDeIntegracao]</c>.</summary>
    public string Tipo { get; set; } = string.Empty;

    public string RoutingKey { get; set; } = string.Empty;

    /// <summary>O envelope inteiro já serializado em JSON.</summary>
    public string Payload { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    public EStatusOutbox Status { get; set; } = EStatusOutbox.Pendente;

    public int Tentativas { get; set; }

    public DateTime CriadoEm { get; set; }

    public DateTime? PublicadoEm { get; set; }

    /// <summary>
    /// Quando a próxima tentativa pode acontecer. É o backoff: sem ele, um
    /// broker fora do ar vira um laço apertado que só gasta CPU e enche o log.
    /// </summary>
    public DateTime ProximaTentativaEm { get; set; }

    public string? UltimoErro { get; set; }
}

public enum EStatusOutbox
{
    Pendente = 0,
    Publicado = 1,

    /// <summary>
    /// Estourou o limite de tentativas. Não some e não bloqueia a fila: fica
    /// visível para alguém olhar, que é o ponto de ter um outbox em tabela e
    /// não em memória.
    /// </summary>
    Falhou = 2,
}

/// <summary>
/// Evento já processado por este serviço.
///
/// O par do outbox do outro lado. O RabbitMQ entrega ao menos uma vez, então a
/// mesma mensagem chega duas vezes sempre que um ack se perde. Sem este
/// registro, "ao menos uma vez" vira orçamento duplicado e cobrança duplicada,
/// que é o pior jeito de descobrir que faltava idempotência.
/// </summary>
public sealed class EventoProcessado
{
    public Guid MensagemId { get; set; }
    public string Tipo { get; set; } = string.Empty;
    public string Consumidor { get; set; } = string.Empty;
    public DateTime ProcessadoEm { get; set; }
}
