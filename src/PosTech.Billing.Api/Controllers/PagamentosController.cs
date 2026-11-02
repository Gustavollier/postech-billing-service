using Microsoft.AspNetCore.Mvc;

namespace PosTech.Billing.Api.Controllers;

/// <summary>
/// O corpo da notificação do Mercado Pago. Só o que interessa: qual recurso
/// mudou. O resto vem da consulta.
/// </summary>
public sealed record NotificacaoRequest(string? Type, DadosDaNotificacao? Data);

public sealed record DadosDaNotificacao(string? Id);

[ApiController]
[Route("api/v1/pagamentos")]
public sealed class PagamentosController : ControllerBase
{
    private readonly IOrcamentoRepositorio _orcamentos;
    private readonly IGatewayDePagamento _pagamentos;
    private readonly ILogger<PagamentosController> _log;

    public PagamentosController(
        IOrcamentoRepositorio orcamentos,
        IGatewayDePagamento pagamentos,
        ILogger<PagamentosController> log)
    {
        _orcamentos = orcamentos;
        _pagamentos = pagamentos;
        _log = log;
    }

    /// <summary>
    /// Webhook do Mercado Pago.
    ///
    /// A notificação diz apenas que algo mudou, e nunca é tratada como verdade:
    /// esta rota é pública, qualquer um pode fazer um POST aqui. Quem diz o que
    /// de fato aconteceu é a consulta ao adquirente logo abaixo.
    ///
    /// Responde 200 mesmo quando não há nada a fazer. O Mercado Pago reenvia
    /// enquanto não receber 2xx, e devolver erro para uma notificação que já
    /// foi processada vira uma tempestade de reenvio.
    /// </summary>
    [HttpPost("webhook")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Webhook([FromBody] NotificacaoRequest? corpo, CancellationToken cancellationToken)
    {
        var idExterno = corpo?.Data?.Id;

        if (string.IsNullOrWhiteSpace(idExterno))
        {
            _log.LogWarning("Webhook sem id de pagamento no corpo.");
            return Ok(new { recebido = true });
        }

        // Primeiro: a referência é de uma cobrança que nós criamos? A rota é
        // pública, e consultar o adquirente com um id qualquer vindo de fora
        // seria usar a nossa credencial a pedido de quem quer que seja. Só ids
        // que já estão gravados aqui seguem para a consulta.
        var orcamento = await _orcamentos.ObterPorReferenciaExternaAsync(idExterno, cancellationToken);

        if (orcamento is null)
        {
            // Pode ser notificação de outro ambiente usando o mesmo webhook, ou
            // a notificação chegando antes de a cobrança terminar de gravar.
            _log.LogWarning("Pagamento {IdExterno} não corresponde a nenhum orçamento daqui.", idExterno);
            return Ok(new { recebido = true });
        }

        var correlationId = CorrelationId();

        Cobranca cobranca;
        try
        {
            cobranca = await _pagamentos.ConsultarAsync(idExterno, cancellationToken);
        }
        catch (HttpRequestException excecao)
        {
            // Aqui sim vale devolver erro: a consulta falhou e o reenvio do
            // Mercado Pago é exatamente o que queremos.
            _log.LogError(excecao, "Falha ao consultar o pagamento {IdExterno}.", idExterno);
            return StatusCode(StatusCodes.Status502BadGateway, new { erro = "Não foi possível consultar o pagamento." });
        }
        catch (ArgumentException excecao)
        {
            // Referência fora do formato do adquirente. Não há o que consultar
            // nem o que reenviar: responder erro só geraria reenvio.
            _log.LogWarning(excecao, "Referência de pagamento inválida: {IdExterno}.", idExterno);
            return Ok(new { recebido = true });
        }

        var mudou = cobranca.Resultado switch
        {
            EResultadoDaCobranca.Aprovada =>
                PagamentoDoOrcamento.Confirmar(orcamento, cobranca, correlationId, DateTime.UtcNow),
            EResultadoDaCobranca.Recusada =>
                PagamentoDoOrcamento.Recusar(
                    orcamento, cobranca.MotivoDaRecusa ?? "Recusado pelo adquirente", correlationId, DateTime.UtcNow),
            _ => false,
        };

        if (mudou)
        {
            await _orcamentos.SalvarAsync(orcamento, cancellationToken);

            _log.LogInformation(
                "Pagamento da ordem {IdOrdem} agora está {Status}.", orcamento.IdOrdem, orcamento.StatusDoPagamento);
        }

        return Ok(new { recebido = true, aplicado = mudou });
    }

    private string CorrelationId() =>
        Request.Headers.TryGetValue("X-Correlation-ID", out var valor) && !string.IsNullOrWhiteSpace(valor)
            ? valor.ToString()
            : Guid.NewGuid().ToString();
}
