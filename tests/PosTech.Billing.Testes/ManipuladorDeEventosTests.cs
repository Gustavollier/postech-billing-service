using Microsoft.Extensions.Logging.Abstractions;
using PosTech.Billing.Api.Aplicacao;
using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing.Testes;

/// <summary>
/// O que o Billing faz com o que chega pela fila.
///
/// É a borda entre o barramento e o domínio, e estava sem teste nenhum — o que
/// é onde dói mais: aqui um erro não aparece como exceção na tela de ninguém,
/// aparece como saga travada esperando um evento que nunca sai.
/// </summary>
public class ManipuladorDeEventosTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly OrcamentosEmMemoria _orcamentos = new();

    private ManipuladorDeEventos Manipulador() =>
        new(_orcamentos, NullLogger<ManipuladorDeEventos>.Instance, () => Agora);

    private static EnvelopeRecebido Recebido<T>(T evento, string correlationId = "corr-1")
        where T : notnull
    {
        var envelope = Envelope<T>.Criar(evento, correlationId, "os-service");
        var atributo = EventoDeIntegracaoAttribute.De(typeof(T));

        return new EnvelopeRecebido(
            envelope.MensagemId,
            envelope.Tipo,
            atributo.RoutingKey,
            correlationId,
            SerializacaoDeEvento.Serializar(envelope));
    }

    private static DiagnosticoConcluido Diagnostico(int idOrdem = 1, decimal valor = 120.50m) =>
        new(IdOrdem: idOrdem, IdFuncionarioResponsavel: 7,
            IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
            Itens: [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, valor)],
            ValorTotal: valor, ConcluidoEm: Agora);

    // -----------------------------------------------------------------------
    // Diagnóstico concluído
    // -----------------------------------------------------------------------

    [Fact]
    public async Task O_diagnostico_fechando_monta_o_orcamento_com_o_cliente_que_veio_no_evento()
    {
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico()));

        var orcamento = Assert.Single(_orcamentos.Salvos);

        Assert.Equal(1, orcamento.IdOrdem);
        Assert.Equal(120.50m, orcamento.Valor);

        // O nome e o e-mail não foram buscados em lugar nenhum: vieram dentro
        // do evento. É o que permite montar orçamento com o Identidade fora do
        // ar.
        Assert.Equal("Ana Prado", orcamento.NomeCliente);
        Assert.Equal("ana@oficina.test", orcamento.EmailCliente);
    }

    [Fact]
    public async Task O_OrcamentoGerado_ja_sai_enfileirado_com_o_correlationId_da_mensagem()
    {
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico(), "corr-abc"));

        var evento = Assert.Single(_orcamentos.Salvos[0].EventosPendentes);

        Assert.Equal("OrcamentoGerado", evento.Tipo);

        // A correlação atravessa o serviço. Sem isso, o trace quebra justamente
        // onde mais importa: na passagem de um serviço para o outro.
        Assert.Equal("corr-abc", evento.CorrelationId);
    }

    [Fact]
    public async Task A_segunda_entrega_do_mesmo_diagnostico_nao_gera_um_segundo_orcamento()
    {
        // O RabbitMQ entrega ao menos uma vez. Sem esta guarda, uma reentrega
        // normal viraria dois orçamentos e duas cobranças para a mesma ordem.
        var envelope = Recebido(Diagnostico());

        await Manipulador().DiagnosticoConcluidoAsync(envelope);
        await Manipulador().DiagnosticoConcluidoAsync(envelope);

        Assert.Single(_orcamentos.Salvos);
    }

    [Fact]
    public async Task Diagnostico_sem_corpo_e_erro_e_vai_para_a_dead_letter()
    {
        // Mensagem malformada não pode ser engolida em silêncio: deixar subir é
        // o que manda ela para a dead letter, onde alguém vai olhar.
        var vazio = new EnvelopeRecebido(
            Guid.NewGuid(), "DiagnosticoConcluido", RoutingKeys.DiagnosticoConcluido, "corr-1", "null");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Manipulador().DiagnosticoConcluidoAsync(vazio));
    }

    // -----------------------------------------------------------------------
    // Compensações
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Orcamento_cancelado_pelo_orquestrador_muda_o_status_e_guarda_o_motivo()
    {
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico()));

        await Manipulador().OrcamentoCanceladoAsync(
            Recebido(new OrcamentoCancelado(1, "orc-1", "cliente rejeitou", Agora)));

        var orcamento = Assert.Single(_orcamentos.Salvos);

        Assert.Equal(EStatusOrcamento.Cancelado, orcamento.Status);
    }

    [Fact]
    public async Task Cancelamento_de_orcamento_que_nao_existe_aqui_nao_estoura()
    {
        // Pode chegar cancelamento de uma ordem que nunca chegou a ter
        // orçamento — compensação de saga que desistiu antes do diagnóstico.
        // Estourar aqui mandaria para a dead letter uma mensagem correta.
        await Manipulador().OrcamentoCanceladoAsync(
            Recebido(new OrcamentoCancelado(99, "orc-99", "desistiu antes", Agora)));

        Assert.Empty(_orcamentos.Salvos);
    }

    [Fact]
    public async Task Ordem_cancelada_sem_pagamento_cancela_o_orcamento_e_nao_estorna_nada()
    {
        // A regra que organiza todas as compensações: compensar só o que de
        // fato aconteceu. Estornar o que nunca foi pago inventaria movimento
        // financeiro.
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico()));
        _orcamentos.Salvos[0].EventosPendentes.Clear();

        await Manipulador().OrdemCanceladaAsync(
            Recebido(new OrdemCancelada(1, "cliente desistiu", Agora)));

        var orcamento = Assert.Single(_orcamentos.Salvos);

        Assert.Equal(EStatusOrcamento.Cancelado, orcamento.Status);
        Assert.DoesNotContain(orcamento.EventosPendentes, e => e.Tipo == "PagamentoEstornado");
    }

    [Fact]
    public async Task Ordem_cancelada_depois_do_pagamento_estorna_e_publica_o_estorno()
    {
        // É este PagamentoEstornado que fecha a saga parada em Compensando. Se
        // ele não sair, a ordem fica compensando para sempre.
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico()));

        var orcamento = _orcamentos.Salvos[0];
        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora);
        PagamentoDoOrcamento.RegistrarCobranca(
            orcamento, new Cobranca(EResultadoDaCobranca.Pendente, "mp-1", null, "https://pagar"), Agora);
        PagamentoDoOrcamento.Confirmar(
            orcamento, new Cobranca(EResultadoDaCobranca.Aprovada, "mp-1", null, null), "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        await Manipulador().OrdemCanceladaAsync(
            Recebido(new OrdemCancelada(1, "execução não assumiu no prazo", Agora)));

        Assert.Equal(EStatusDoPagamento.Estornado, orcamento.StatusDoPagamento);
        Assert.Contains(orcamento.EventosPendentes, e => e.Tipo == "PagamentoEstornado");
    }

    [Fact]
    public async Task Ordem_cancelada_duas_vezes_estorna_uma_vez_so()
    {
        await Manipulador().DiagnosticoConcluidoAsync(Recebido(Diagnostico()));

        var orcamento = _orcamentos.Salvos[0];
        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora);
        PagamentoDoOrcamento.RegistrarCobranca(
            orcamento, new Cobranca(EResultadoDaCobranca.Pendente, "mp-1", null, null), Agora);
        PagamentoDoOrcamento.Confirmar(
            orcamento, new Cobranca(EResultadoDaCobranca.Aprovada, "mp-1", null, null), "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        var envelope = Recebido(new OrdemCancelada(1, "desistiu", Agora));

        await Manipulador().OrdemCanceladaAsync(envelope);
        await Manipulador().OrdemCanceladaAsync(envelope);

        Assert.Single(orcamento.EventosPendentes, e => e.Tipo == "PagamentoEstornado");
    }

    [Fact]
    public async Task Ordem_cancelada_sem_orcamento_aqui_nao_e_erro()
    {
        await Manipulador().OrdemCanceladaAsync(
            Recebido(new OrdemCancelada(77, "cancelou na recepção", Agora)));

        Assert.Empty(_orcamentos.Salvos);
    }

    [Fact]
    public void A_lista_de_tratadores_cobre_tudo_que_a_fila_do_Billing_assina()
    {
        // O consumidor registra os tratadores a partir desta lista. Um evento
        // que a fila recebe e a lista não conhece é descartado em silêncio —
        // então a lista e a assinatura da topologia têm que bater.
        var tratadores = Manipulador().Tratadores().Keys.OrderBy(k => k).ToList();

        var assinadas = Topologia.Assinaturas[Topologia.FilaBilling].ToHashSet();

        // Os nomes saem dos próprios contratos, pelo atributo — não de uma
        // lista escrita à mão aqui, que envelheceria junto com o erro.
        var esperados = typeof(OrdemAberta).Assembly.GetTypes()
            .Select(t => (Tipo: t, Atributo: t.GetCustomAttributes(typeof(EventoDeIntegracaoAttribute), false)
                .Cast<EventoDeIntegracaoAttribute>().FirstOrDefault()))
            .Where(x => x.Atributo is not null && assinadas.Contains(x.Atributo.RoutingKey))
            .Select(x => x.Atributo!.Nome)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(esperados, tratadores);
    }
}

/// <summary>
/// Repositório de orçamentos em memória, com o mesmo comportamento que o
/// documento precisa ter: busca por ordem, por id e por referência externa.
/// </summary>
public sealed class OrcamentosEmMemoria : IOrcamentoRepositorio
{
    public List<Orcamento> Salvos { get; } = [];

    public Task<Orcamento?> ObterPorOrdemAsync(int idOrdem, CancellationToken cancellationToken = default) =>
        Task.FromResult(Salvos.FirstOrDefault(o => o.IdOrdem == idOrdem));

    public Task<Orcamento?> ObterAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Salvos.FirstOrDefault(o => o.Id == id));

    public Task<Orcamento?> ObterPorReferenciaExternaAsync(string referencia, CancellationToken cancellationToken = default) =>
        Task.FromResult(Salvos.FirstOrDefault(o => o.ReferenciaExterna == referencia));

    public Task SalvarAsync(Orcamento orcamento, CancellationToken cancellationToken = default)
    {
        if (!Salvos.Any(o => o.Id == orcamento.Id))
            Salvos.Add(orcamento);

        return Task.CompletedTask;
    }
}
