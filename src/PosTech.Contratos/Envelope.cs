using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PosTech.Contratos;

/// <summary>
/// Envelope comum a todo evento que trafega no barramento.
///
/// O que importa aqui é o <see cref="CorrelationId"/>. O tracer do Datadog segue
/// chamada HTTP sozinho, mas não atravessa o RabbitMQ: do outro lado da fila ele
/// começa um trace novo, e o rastro do fluxo distribuído quebra exatamente onde
/// a Fase 4 pede para mostrá-lo funcionando. O envelope carrega o mesmo
/// X-Correlation-ID que o gateway cria na borda, e o consumidor o devolve ao
/// escopo de log antes de processar. É isso que costura as duas pontas.
///
/// <see cref="MensagemId"/> existe para idempotência: o broker garante entrega
/// ao menos uma vez, então o mesmo evento chega repetido em falha de rede. O
/// consumidor registra o id processado e descarta a repetição.
/// </summary>
public sealed record Envelope<T>(
    [property: JsonPropertyName("mensagemId")] Guid MensagemId,
    [property: JsonPropertyName("tipo")] string Tipo,
    [property: JsonPropertyName("versao")] int Versao,
    [property: JsonPropertyName("ocorridoEm")] DateTime OcorridoEm,
    [property: JsonPropertyName("correlationId")] string CorrelationId,
    [property: JsonPropertyName("origem")] string Origem,
    [property: JsonPropertyName("dados")] T Dados)
    where T : notnull
{
    /// <summary>
    /// Monta o envelope de um evento novo.
    /// </summary>
    /// <param name="dados">O corpo do evento, um dos tipos de <c>Eventos.cs</c>.</param>
    /// <param name="correlationId">
    /// O X-Correlation-ID do fluxo. Quem publica dentro de um request HTTP usa o
    /// cabeçalho que o gateway mandou; quem publica reagindo a outro evento
    /// repassa o correlationId do envelope que consumiu, e não cria um novo.
    /// </param>
    /// <param name="origem">Nome do serviço que publica, por exemplo "os-service".</param>
    public static Envelope<T> Criar(T dados, string correlationId, string origem)
    {
        ArgumentNullException.ThrowIfNull(dados);

        if (string.IsNullOrWhiteSpace(correlationId))
            throw new ArgumentException("CorrelationId é obrigatório no envelope.", nameof(correlationId));

        if (string.IsNullOrWhiteSpace(origem))
            throw new ArgumentException("Origem é obrigatória no envelope.", nameof(origem));

        return new Envelope<T>(
            MensagemId: Guid.NewGuid(),
            Tipo: TipoDe(typeof(T)),
            Versao: VersaoDe(typeof(T)),
            OcorridoEm: DateTime.UtcNow,
            CorrelationId: correlationId,
            Origem: origem,
            Dados: dados);
    }

    /// <summary>
    /// O nome do tipo é o contrato público do evento. Vem do atributo e não de
    /// <c>typeof(T).Name</c> de propósito: renomear a classe em C# não pode
    /// quebrar quem já consome a fila.
    /// </summary>
    public static string TipoDe(Type tipo) =>
        EventoDeIntegracaoAttribute.De(tipo).Nome;

    public static int VersaoDe(Type tipo) =>
        EventoDeIntegracaoAttribute.De(tipo).Versao;
}

/// <summary>
/// Marca um record como evento de integração e fixa o nome e a versão que vão
/// no envelope. Sem o atributo, o tipo não pode ser publicado.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class EventoDeIntegracaoAttribute : Attribute
{
    public EventoDeIntegracaoAttribute(string nome, string routingKey, int versao = 1)
    {
        if (string.IsNullOrWhiteSpace(nome))
            throw new ArgumentException("Nome do evento é obrigatório.", nameof(nome));

        if (string.IsNullOrWhiteSpace(routingKey))
            throw new ArgumentException("RoutingKey é obrigatória.", nameof(routingKey));

        if (versao < 1)
            throw new ArgumentOutOfRangeException(nameof(versao), "Versão começa em 1.");

        Nome = nome;
        RoutingKey = routingKey;
        Versao = versao;
    }

    public string Nome { get; }
    public string RoutingKey { get; }
    public int Versao { get; }

    public static EventoDeIntegracaoAttribute De(Type tipo)
    {
        ArgumentNullException.ThrowIfNull(tipo);

        return GetCustomAttribute(tipo, typeof(EventoDeIntegracaoAttribute)) as EventoDeIntegracaoAttribute
            ?? throw new InvalidOperationException(
                $"O tipo {tipo.Name} não está marcado com [EventoDeIntegracao] e não pode ser publicado.");
    }
}

/// <summary>
/// Serialização do envelope. Uma única configuração de JSON nos dois lados do
/// barramento, para o consumidor não depender de como o publicador se configurou.
/// </summary>
public static class SerializacaoDeEvento
{
    public static readonly JsonSerializerOptions Opcoes = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,

        // Sem isto, "Troca de óleo" vira "Troca de \u00F3leo" no corpo da
        // mensagem. O JSON continua válido, mas o payload é lido por humanos no
        // painel do RabbitMQ e no Compass — e é essa tela que o vídeo mostra.
        // O "Unsafe" do nome é sobre embutir JSON direto em HTML, que não é o
        // nosso caso: isto aqui é transporte entre serviços.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,

        // Enum como nome, e não como número. Número é imune a renomear mas
        // quebra se alguém inserir um valor no meio do enum, que é o erro mais
        // fácil de cometer. Nome também deixa a mensagem legível sem consultar
        // o código, o que vale na hora de explicar o fluxo.
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serializar<T>(Envelope<T> envelope) where T : notnull =>
        JsonSerializer.Serialize(envelope, Opcoes);

    public static Envelope<T>? Desserializar<T>(string json) where T : notnull =>
        JsonSerializer.Deserialize<Envelope<T>>(json, Opcoes);
}
