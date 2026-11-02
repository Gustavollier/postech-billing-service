using PosTech.Billing;

namespace PosTech.Billing.Api.Infra;

/// <summary>
/// Gateway de mentira para quando não há token configurado.
///
/// Recusa com uma mensagem que diz o que falta, em vez de estourar
/// NullReference no meio da aprovação. O serviço continua montando orçamento e
/// respondendo à fila normalmente — só a cobrança não funciona.
/// </summary>
public sealed class GatewayIndisponivel : IGatewayDePagamento
{
    private const string Motivo =
        "Mercado Pago não configurado: defina MercadoPago__AccessToken.";

    public Task<Cobranca> CriarCobrancaAsync(Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Cobranca(EResultadoDaCobranca.Recusada, string.Empty, Motivo, null));

    public Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default) =>
        Task.FromResult(new Cobranca(EResultadoDaCobranca.Recusada, referenciaExterna, Motivo, null));
}
