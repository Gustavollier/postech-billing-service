using Microsoft.AspNetCore.Mvc;

namespace PosTech.Billing.Api.Controllers;

public sealed record RejeitarRequest(string Motivo);

[ApiController]
[Route("api/v1/orcamentos")]
public sealed class OrcamentosController : ControllerBase
{
    private readonly IOrcamentoRepositorio _orcamentos;
    private readonly IGatewayDePagamento _pagamentos;
    private readonly ILogger<OrcamentosController> _log;

    public OrcamentosController(
        IOrcamentoRepositorio orcamentos,
        IGatewayDePagamento pagamentos,
        ILogger<OrcamentosController> log)
    {
        _orcamentos = orcamentos;
        _pagamentos = pagamentos;
        _log = log;
    }

    [HttpGet("os/{idOrdem:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PorOrdem(int idOrdem, CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentos.ObterPorOrdemAsync(idOrdem, cancellationToken);

        return orcamento is null ? NotFound() : Ok(Mapear(orcamento));
    }

    /// <summary>
    /// O cliente aprovou. Duas coisas acontecem, nesta ordem: o orçamento muda
    /// de status com o evento enfileirado na mesma escrita, e só então a
    /// cobrança é criada no adquirente.
    ///
    /// A ordem importa. Cobrar antes de registrar a aprovação deixaria uma
    /// cobrança órfã se o banco falhasse logo depois.
    /// </summary>
    [HttpPost("os/{idOrdem:int}/aprovar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Aprovar(int idOrdem, CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentos.ObterPorOrdemAsync(idOrdem, cancellationToken);

        if (orcamento is null)
            return NotFound();

        if (orcamento.Status != EStatusOrcamento.Pendente)
            return Conflict(new { erro = $"Este orçamento já está {orcamento.Status}." });

        var correlationId = CorrelationId();

        MontadorDeOrcamento.Aprovar(orcamento, correlationId, DateTime.UtcNow);
        await _orcamentos.SalvarAsync(orcamento, cancellationToken);

        try
        {
            var cobranca = await _pagamentos.CriarCobrancaAsync(orcamento, correlationId, cancellationToken);

            PagamentoDoOrcamento.RegistrarCobranca(orcamento, cobranca, DateTime.UtcNow);

            // Em sandbox a cobrança pode já voltar aprovada ou recusada, sem
            // webhook nenhum. Tratar as duas aqui evita a demonstração
            // depender de uma notificação que pode não vir.
            if (cobranca.Resultado == EResultadoDaCobranca.Aprovada)
                PagamentoDoOrcamento.Confirmar(orcamento, cobranca, correlationId, DateTime.UtcNow);
            else if (cobranca.Resultado == EResultadoDaCobranca.Recusada)
                PagamentoDoOrcamento.Recusar(orcamento, cobranca.MotivoDaRecusa ?? "Recusado pelo adquirente", correlationId, DateTime.UtcNow);

            await _orcamentos.SalvarAsync(orcamento, cancellationToken);
        }
        catch (HttpRequestException excecao)
        {
            // A aprovação já está gravada e o OrcamentoAprovado já saiu. A
            // cobrança falhou, e o cliente pode tentar de novo: devolver 502
            // aqui é honesto, e não desfaz o que já valeu.
            _log.LogError(excecao, "Falha ao criar cobrança da ordem {IdOrdem}.", idOrdem);

            return StatusCode(StatusCodes.Status502BadGateway, new
            {
                erro = "O orçamento foi aprovado, mas a cobrança não pôde ser criada. Tente pagar novamente.",
            });
        }

        return Ok(Mapear(orcamento));
    }

    [HttpPost("os/{idOrdem:int}/rejeitar")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Rejeitar(int idOrdem, [FromBody] RejeitarRequest corpo, CancellationToken cancellationToken)
    {
        var orcamento = await _orcamentos.ObterPorOrdemAsync(idOrdem, cancellationToken);

        if (orcamento is null)
            return NotFound();

        if (orcamento.Status != EStatusOrcamento.Pendente)
            return Conflict(new { erro = $"Este orçamento já está {orcamento.Status}." });

        MontadorDeOrcamento.Rejeitar(
            orcamento, corpo?.Motivo ?? "Rejeitado pelo cliente", CorrelationId(), DateTime.UtcNow);

        await _orcamentos.SalvarAsync(orcamento, cancellationToken);

        return Ok(Mapear(orcamento));
    }

    private static object Mapear(Orcamento o) => new
    {
        id = o.Id,
        idOrdem = o.IdOrdem,
        nomeCliente = o.NomeCliente,
        valor = o.Valor,
        status = o.Status.ToString(),
        statusDoPagamento = o.StatusDoPagamento.ToString(),
        urlDePagamento = o.UrlDePagamento,

        // O id da cobrança no Mercado Pago. É o que a notificação do webhook
        // traz, e é por ele que se acha a cobrança no painel do adquirente.
        referenciaExterna = o.ReferenciaExterna,
        motivoDaRecusa = o.MotivoDaRecusa ?? o.MotivoDaRecusaDoPagamento,
        itens = o.Itens.Select(i => new
        {
            descricao = i.Descricao,
            tipo = i.Tipo.ToString(),
            quantidade = i.Quantidade,
            valorUnitario = i.ValorUnitario,
            subtotal = i.Subtotal,
        }),
        criadoEm = o.CriadoEm,
        pagoEm = o.PagoEm,
    };

    private string CorrelationId() =>
        Request.Headers.TryGetValue("X-Correlation-ID", out var valor) && !string.IsNullOrWhiteSpace(valor)
            ? valor.ToString()
            : Guid.NewGuid().ToString();
}
