using PosTech.Billing;

namespace PosTech.Billing.Api.Infra;

/// <summary>
/// Gateway de mentira para quando não há token configurado.
///
/// Falha explicitamente com uma mensagem que diz o que falta, em vez de
/// transformar uma configuração ausente em recusa de pagamento. O serviço
/// continua montando orçamento e respondendo à fila normalmente — só a
/// cobrança não funciona.
/// </summary>
public sealed class GatewayIndisponivelException : InvalidOperationException
{
    public GatewayIndisponivelException()
        : base("Mercado Pago não configurado: defina MercadoPago__AccessToken.")
    {
    }
}

public sealed class GatewayIndisponivel : IGatewayDePagamento
{
    public Task<Cobranca> CriarCobrancaAsync(Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default) =>
        Task.FromException<Cobranca>(new GatewayIndisponivelException());

    public Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default) =>
        Task.FromException<Cobranca>(new GatewayIndisponivelException());
}
