using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace PosTech.Billing;

public enum EResultadoDaCobranca
{
    /// <summary>Cobrança criada e esperando o cliente pagar.</summary>
    Pendente = 0,

    Aprovada = 1,
    Recusada = 2,
}

public sealed record Cobranca(
    EResultadoDaCobranca Resultado,
    string ReferenciaExterna,
    string? MotivoDaRecusa,
    string? UrlDePagamento);

/// <summary>
/// Quem cobra o cliente.
///
/// A interface existe para o Billing não depender do Mercado Pago: o caso de
/// uso fala com ela, e os testes trocam por um dublê sem subir HTTP. Trocar de
/// adquirente seria uma implementação nova, não uma reescrita do serviço.
/// </summary>
public interface IGatewayDePagamento
{
    Task<Cobranca> CriarCobrancaAsync(Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confere o estado real da cobrança no adquirente.
    ///
    /// Existe porque webhook não é fonte de verdade: qualquer um pode fazer um
    /// POST na rota, e a notificação pode chegar fora de ordem. O webhook só
    /// avisa que algo mudou; quem diz o que mudou é esta consulta.
    /// </summary>
    Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default);
}

/// <summary>
/// Mercado Pago, API de Orders (<c>/v1/orders</c>).
///
/// Era a API de pagamentos (<c>/v1/payments</c>) até a Fase 4. Nas contas
/// novas do Mercado Pago, a credencial de teste de uma aplicação de Checkout
/// Transparente só funciona na API de Orders: em <c>/v1/payments</c> ela
/// devolve 401 "Unauthorized use of live credentials", com qualquer pagador.
/// Foi o que travou o pagamento no sandbox até a troca, e um pedido de Pix
/// criado direto na API de Orders, com o mesmo token, confirmou a causa.
///
/// Cada cobrança é um pedido com uma transação Pix. O id do pedido (prefixo
/// <c>ORD</c>) é a referência externa: é ele que o webhook traz e que a
/// consulta usa.
/// </summary>
public sealed class GatewayMercadoPago : IGatewayDePagamento
{
    public const string NomeDoHttpClient = "mercado-pago";

    /// <summary>Id de pedido da API de Orders: "ORD" e letras maiúsculas ou dígitos.</summary>
    private const int TamanhoMaximoDaReferencia = 64;

    private readonly HttpClient _http;
    private readonly string _accessToken;
    private readonly string? _nomeDoPagadorDeTeste;

    /// <param name="nomeDoPagadorDeTeste">
    /// Só para sandbox. O Mercado Pago decide o resultado do pagamento de teste
    /// pelo primeiro nome do pagador: com <c>APRO</c>, o Pix é aprovado sozinho
    /// logo depois de criado, e o webhook chega como chegaria com um pagamento
    /// de verdade. Sem ele, o Pix de teste fica esperando para sempre, porque
    /// ninguém paga. Em produção fica vazio e vai o nome do cliente.
    /// </param>
    public GatewayMercadoPago(HttpClient http, string accessToken, string? nomeDoPagadorDeTeste = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token do Mercado Pago é obrigatório.", nameof(accessToken));

        _accessToken = accessToken;
        _nomeDoPagadorDeTeste = string.IsNullOrWhiteSpace(nomeDoPagadorDeTeste) ? null : nomeDoPagadorDeTeste.Trim();
    }

    public async Task<Cobranca> CriarCobrancaAsync(
        Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        // A API de Orders recebe valores como texto com duas casas e ponto,
        // independentemente da cultura do servidor.
        var valor = orcamento.Valor.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

        var pedido = new
        {
            type = "online",
            processing_mode = "automatic",
            total_amount = valor,
            // O id do orçamento volta na notificação e na consulta. É por ele
            // que se acha a ordem sem depender de outro estado.
            external_reference = orcamento.Id,
            payer = new
            {
                email = orcamento.EmailCliente,
                first_name = _nomeDoPagadorDeTeste ?? PrimeiroNome(orcamento.NomeCliente),
            },
            transactions = new
            {
                payments = new[]
                {
                    new
                    {
                        amount = valor,
                        payment_method = new { id = "pix", type = "bank_transfer" },
                    },
                },
            },
        };

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "/v1/orders")
        {
            Content = JsonContent.Create(pedido),
        };

        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        // Idempotência do lado do adquirente: se a requisição for repetida por
        // retry de rede, o Mercado Pago devolve o mesmo pedido em vez de criar
        // outro. Sem isso, um timeout vira cobrança em dobro.
        requisicao.Headers.Add("X-Idempotency-Key", orcamento.Id);
        requisicao.Headers.Add("X-Correlation-ID", correlationId);

        using var resposta = await _http.SendAsync(requisicao, cancellationToken).ConfigureAwait(false);

        var corpo = await LerAsync(resposta, cancellationToken).ConfigureAwait(false);

        return Traduzir(corpo);
    }

    public async Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default)
    {
        // O id vira caminho de URL numa chamada que leva o nosso token. Fora do
        // formato de id de pedido — "../", "?", "/" — ele mudaria o endpoint
        // chamado com a nossa credencial.
        if (!ReferenciaValida(referenciaExterna))
            throw new ArgumentException($"Referência de pagamento inválida: '{referenciaExterna}'.", nameof(referenciaExterna));

        // O escape é redundante depois da validação acima, mas é o sanitizador
        // que a análise de fluxo do SonarCloud reconhece, e não custa nada.
        using var requisicao = new HttpRequestMessage(HttpMethod.Get, $"/v1/orders/{Uri.EscapeDataString(referenciaExterna)}");
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var resposta = await _http.SendAsync(requisicao, cancellationToken).ConfigureAwait(false);

        var corpo = await LerAsync(resposta, cancellationToken).ConfigureAwait(false);

        return Traduzir(corpo);
    }

    private static async Task<RespostaDoPedido> LerAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        if (!resposta.IsSuccessStatusCode)
        {
            var texto = await resposta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new HttpRequestException(
                $"Mercado Pago respondeu {(int)resposta.StatusCode}: {Resumir(texto)}");
        }

        return await resposta.Content.ReadFromJsonAsync<RespostaDoPedido>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Mercado Pago respondeu com corpo vazio.");
    }

    /// <summary>
    /// "ORD" seguido de letras maiúsculas ou dígitos: o formato do id de pedido
    /// da API de Orders (por exemplo, ORD01JP84C939T20S0P1DN382FQ6K).
    /// </summary>
    public static bool ReferenciaValida(string? referencia) =>
        referencia is { Length: > 3 and <= TamanhoMaximoDaReferencia }
        && referencia.StartsWith("ORD", StringComparison.Ordinal)
        && referencia.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c));

    /// <summary>
    /// Traduz o vocabulário do adquirente para o do domínio.
    ///
    /// Fica isolado num lugar só: se a tradução estivesse espalhada, uma string
    /// nova do Mercado Pago viraria um bug silencioso em cada ponto que a
    /// comparasse.
    /// </summary>
    internal static Cobranca Traduzir(RespostaDoPedido corpo)
    {
        var resultado = corpo.Status switch
        {
            // processed/accredited é o pago. processed/partially_refunded
            // também foi pago, e o estorno parcial não desfaz a confirmação.
            "processed" => EResultadoDaCobranca.Aprovada,
            "failed" or "canceled" => EResultadoDaCobranca.Recusada,

            // created, action_required (Pix esperando a transferência),
            // processing, refunded e qualquer status novo: não é um fato novo
            // de pagamento. Tratar desconhecido como recusa cancelaria ordem
            // paga; refunded é consequência de um estorno nosso, e não um
            // pagamento recusado.
            _ => EResultadoDaCobranca.Pendente,
        };

        var pix = corpo.Transactions?.Payments?.FirstOrDefault()?.PaymentMethod;

        return new Cobranca(
            resultado,
            corpo.Id ?? string.Empty,
            resultado == EResultadoDaCobranca.Recusada ? corpo.StatusDetail ?? corpo.Status : null,
            pix?.TicketUrl);
    }

    private static string PrimeiroNome(string? nome) =>
        (nome ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Cliente";

    private static string Resumir(string texto) =>
        texto.Length <= 500 ? texto : texto[..500];

    internal sealed record RespostaDoPedido
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; init; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; init; }
        [JsonPropertyName("transactions")] public Transacoes? Transactions { get; init; }
    }

    internal sealed record Transacoes
    {
        [JsonPropertyName("payments")] public List<PagamentoDoPedido>? Payments { get; init; }
    }

    internal sealed record PagamentoDoPedido
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("payment_method")] public MeioDePagamento? PaymentMethod { get; init; }
    }

    internal sealed record MeioDePagamento
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("ticket_url")] public string? TicketUrl { get; init; }
        [JsonPropertyName("qr_code")] public string? QrCode { get; init; }
    }
}
