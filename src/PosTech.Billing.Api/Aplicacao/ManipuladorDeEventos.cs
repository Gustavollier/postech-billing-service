using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing.Api.Aplicacao;

/// <summary>
/// O que o Billing faz quando o barramento traz alguma coisa.
///
/// Três eventos interessam: o diagnóstico fechando (monta o orçamento), o
/// orçamento sendo cancelado pelo orquestrador, e a ordem sendo cancelada (que
/// além de cancelar pode exigir estorno).
/// </summary>
public sealed class ManipuladorDeEventos
{
    private readonly IOrcamentoRepositorio _orcamentos;
    private readonly ILogger<ManipuladorDeEventos> _log;
    private readonly Func<DateTime> _agora;

    public ManipuladorDeEventos(
        IOrcamentoRepositorio orcamentos,
        ILogger<ManipuladorDeEventos> log,
        Func<DateTime>? agora = null)
    {
        _orcamentos = orcamentos;
        _log = log;
        _agora = agora ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// O diagnóstico fechou: monta o orçamento com os itens que vieram dentro
    /// do evento, sem perguntar nada ao Execução nem ao Identidade.
    /// </summary>
    public async Task DiagnosticoConcluidoAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<DiagnosticoConcluido>()
            ?? throw new InvalidOperationException("DiagnosticoConcluido veio sem corpo.");

        // O índice único por ordem já impede dois orçamentos, mas conferir
        // antes evita transformar uma reentrega normal numa exceção de banco.
        if (await _orcamentos.ObterPorOrdemAsync(evento.IdOrdem, cancellationToken).ConfigureAwait(false) is not null)
        {
            _log.LogInformation("A ordem {IdOrdem} já tem orçamento. Nada a fazer.", evento.IdOrdem);
            return;
        }

        // O nome e o e-mail do cliente vieram no OrdemAberta e ficaram com o
        // Execução, que os repassa aqui. Dado duplicado de propósito: sem isso,
        // montar orçamento passaria a depender do Identidade estar no ar.
        var cliente = new DadosDoCliente(evento.IdCliente, evento.NomeCliente, evento.EmailCliente);

        var orcamento = MontadorDeOrcamento.Montar(evento, cliente, envelope.CorrelationId, _agora());

        await _orcamentos.SalvarAsync(orcamento, cancellationToken).ConfigureAwait(false);

        _log.LogInformation(
            "Orçamento {IdOrcamento} montado para a ordem {IdOrdem}, valor {Valor}.",
            orcamento.Id, orcamento.IdOrdem, orcamento.Valor);
    }

    /// <summary>
    /// Compensação do orquestrador: o orçamento deixa de valer.
    /// </summary>
    public async Task OrcamentoCanceladoAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<OrcamentoCancelado>()
            ?? throw new InvalidOperationException("OrcamentoCancelado veio sem corpo.");

        var orcamento = await _orcamentos.ObterPorOrdemAsync(evento.IdOrdem, cancellationToken).ConfigureAwait(false);

        if (orcamento is null)
        {
            _log.LogWarning("Cancelamento de orçamento para a ordem {IdOrdem}, que não tem orçamento aqui.", evento.IdOrdem);
            return;
        }

        MontadorDeOrcamento.Cancelar(orcamento, evento.Motivo, _agora());

        await _orcamentos.SalvarAsync(orcamento, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A ordem foi cancelada. Cancela o orçamento e, se houve pagamento,
    /// estorna — e é o estorno que publica <c>PagamentoEstornado</c>, fechando
    /// a saga que ficou em Compensando esperando por ele.
    /// </summary>
    public async Task OrdemCanceladaAsync(EnvelopeRecebido envelope, CancellationToken cancellationToken = default)
    {
        var evento = envelope.Ler<OrdemCancelada>()
            ?? throw new InvalidOperationException("OrdemCancelada veio sem corpo.");

        var orcamento = await _orcamentos.ObterPorOrdemAsync(evento.IdOrdem, cancellationToken).ConfigureAwait(false);

        if (orcamento is null)
        {
            _log.LogInformation("Ordem {IdOrdem} cancelada sem orçamento neste serviço.", evento.IdOrdem);
            return;
        }

        MontadorDeOrcamento.Cancelar(orcamento, evento.Motivo, _agora());

        var estornou = PagamentoDoOrcamento.Estornar(
            orcamento, evento.Motivo, envelope.CorrelationId, _agora());

        await _orcamentos.SalvarAsync(orcamento, cancellationToken).ConfigureAwait(false);

        if (estornou)
            _log.LogWarning("Pagamento da ordem {IdOrdem} estornado: {Motivo}.", evento.IdOrdem, evento.Motivo);
    }

    /// <summary>Os tratadores que o consumidor registra na fila do Billing.</summary>
    public IReadOnlyDictionary<string, TratadorDeEvento> Tratadores() => new Dictionary<string, TratadorDeEvento>
    {
        ["DiagnosticoConcluido"] = DiagnosticoConcluidoAsync,
        ["OrcamentoCancelado"] = OrcamentoCanceladoAsync,
        ["OrdemCancelada"] = OrdemCanceladaAsync,
    };
}
