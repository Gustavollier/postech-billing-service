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
/// Mercado Pago, API de pagamentos.
///
/// Implementada contra o contrato documentado da v1. Em sandbox, o resultado é
/// decidido pelo cartão de teste usado, o que dá um jeito previsível de
/// demonstrar a recusa no vídeo sem simular nada.
/// </summary>
public sealed class GatewayMercadoPago : IGatewayDePagamento
{
    public const string NomeDoHttpClient = "mercado-pago";

    private readonly HttpClient _http;
    private readonly string _accessToken;

    public GatewayMercadoPago(HttpClient http, string accessToken)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));

        if (string.IsNullOrWhiteSpace(accessToken))
            throw new ArgumentException("Access token do Mercado Pago é obrigatório.", nameof(accessToken));

        _accessToken = accessToken;
    }

    public async Task<Cobranca> CriarCobrancaAsync(
        Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        var pedido = new
        {
            transaction_amount = orcamento.Valor,
            description = $"Ordem de serviço {orcamento.IdOrdem}",
            payment_method_id = "pix",
            payer = new { email = orcamento.EmailCliente },
            // O id do orçamento volta na notificação. É por ele que o webhook
            // sabe de qual ordem está falando, sem depender de guardar estado
            // entre a criação e a notificação.
            external_reference = orcamento.Id,
        };

        using var requisicao = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(pedido),
        };

        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        // Idempotência do lado do adquirente: se a requisição for repetida por
        // retry de rede, o Mercado Pago devolve a mesma cobrança em vez de
        // criar outra. Sem isso, um timeout vira cobrança em dobro.
        requisicao.Headers.Add("X-Idempotency-Key", orcamento.Id);
        requisicao.Headers.Add("X-Correlation-ID", correlationId);

        using var resposta = await _http.SendAsync(requisicao, cancellationToken).ConfigureAwait(false);

        var corpo = await LerAsync(resposta, cancellationToken).ConfigureAwait(false);

        return Traduzir(corpo);
    }

    public async Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default)
    {
        // O id vira caminho de URL numa chamada que leva o nosso token. Id de
        // pagamento do Mercado Pago é numérico; qualquer outra coisa — "../",
        // "?", "/" — mudaria o endpoint chamado com a nossa credencial.
        if (!ReferenciaValida(referenciaExterna))
            throw new ArgumentException($"Referência de pagamento inválida: '{referenciaExterna}'.", nameof(referenciaExterna));

        using var requisicao = new HttpRequestMessage(HttpMethod.Get, $"/v1/payments/{referenciaExterna}");
        requisicao.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);

        using var resposta = await _http.SendAsync(requisicao, cancellationToken).ConfigureAwait(false);

        var corpo = await LerAsync(resposta, cancellationToken).ConfigureAwait(false);

        return Traduzir(corpo);
    }

    private static async Task<RespostaDePagamento> LerAsync(HttpResponseMessage resposta, CancellationToken cancellationToken)
    {
        if (!resposta.IsSuccessStatusCode)
        {
            var texto = await resposta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            throw new HttpRequestException(
                $"Mercado Pago respondeu {(int)resposta.StatusCode}: {Resumir(texto)}");
        }

        return await resposta.Content.ReadFromJsonAsync<RespostaDePagamento>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("Mercado Pago respondeu com corpo vazio.");
    }

    /// <summary>
    /// Traduz o vocabulário do adquirente para o do domínio.
    ///
    /// Fica isolado num lugar só: se a tradução estivesse espalhada, uma string
    /// nova do Mercado Pago viraria um bug silencioso em cada ponto que a
    /// comparasse.
    /// </summary>
    /// <summary>Só dígitos, até 20: o formato do id de pagamento do Mercado Pago.</summary>
    public static bool ReferenciaValida(string? referencia) =>
        referencia is { Length: > 0 and <= 20 } && referencia.All(char.IsAsciiDigit);

    internal static Cobranca Traduzir(RespostaDePagamento corpo)
    {
        var resultado = corpo.Status switch
        {
            "approved" => EResultadoDaCobranca.Aprovada,
            "rejected" or "cancelled" => EResultadoDaCobranca.Recusada,
            // pending, in_process, authorized e qualquer status novo: ainda não
            // é não. Tratar desconhecido como recusa cancelaria ordem paga.
            _ => EResultadoDaCobranca.Pendente,
        };

        return new Cobranca(
            resultado,
            corpo.Id?.ToString() ?? string.Empty,
            resultado == EResultadoDaCobranca.Recusada ? corpo.StatusDetail ?? corpo.Status : null,
            corpo.PointOfInteraction?.TransactionData?.TicketUrl);
    }

    private static string Resumir(string texto) =>
        texto.Length <= 500 ? texto : texto[..500];

    internal sealed record RespostaDePagamento
    {
        [JsonPropertyName("id")] public long? Id { get; init; }
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("status_detail")] public string? StatusDetail { get; init; }
        [JsonPropertyName("external_reference")] public string? ExternalReference { get; init; }
        [JsonPropertyName("point_of_interaction")] public PontoDeInteracao? PointOfInteraction { get; init; }
    }

    internal sealed record PontoDeInteracao
    {
        [JsonPropertyName("transaction_data")] public DadosDaTransacao? TransactionData { get; init; }
    }

    internal sealed record DadosDaTransacao
    {
        [JsonPropertyName("ticket_url")] public string? TicketUrl { get; init; }
    }
}
