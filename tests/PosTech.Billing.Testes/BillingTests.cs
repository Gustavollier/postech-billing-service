using MongoDB.Driver;
using PosTech.Billing;
using PosTech.Contratos;
using PosTech.Mensageria;
using Testcontainers.MongoDb;

namespace PosTech.Billing.Testes;

public class MontadorDeOrcamentoTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static readonly DadosDoCliente Cliente = new(10, "Ana Prado", "ana@oficina.test");

    private static DiagnosticoConcluido Diagnostico(params ItemDaOrdem[] itens) =>
        new(IdOrdem: 1, IdFuncionarioResponsavel: 7,
            IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
            Itens: itens,
            ValorTotal: itens.Sum(i => i.Quantidade * i.ValorUnitario), ConcluidoEm: Agora);

    [Fact]
    public void O_valor_sai_da_soma_dos_subtotais_e_nao_do_total_que_veio_no_evento()
    {
        // O Billing recalcula em vez de confiar no ValorTotal do evento. Se o
        // Execução errar a soma, o erro para aqui em vez de virar cobrança.
        var diagnostico = Diagnostico(
            new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m),
            new ItemDaOrdem("Filtro de óleo", ETipoItem.Peca, 2, 38.90m));

        var orcamento = MontadorDeOrcamento.Montar(diagnostico, Cliente, "corr-1", Agora);

        Assert.Equal(198.30m, orcamento.Valor);
        Assert.Equal(120.50m, orcamento.Itens[0].Subtotal);
        Assert.Equal(77.80m, orcamento.Itens[1].Subtotal);
    }

    [Fact]
    public void O_orcamento_guarda_o_preco_do_dia_em_que_foi_montado()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            Diagnostico(new ItemDaOrdem("Pastilha", ETipoItem.Peca, 4, 89.90m)), Cliente, "corr-1", Agora);

        // O preço viajou no evento e foi copiado para o documento. Uma mudança
        // na tabela de peças não pode reescrever orçamento já enviado.
        Assert.Equal(89.90m, orcamento.Itens[0].ValorUnitario);
        Assert.Equal(359.60m, orcamento.Valor);
    }

    [Fact]
    public void Montar_ja_deixa_o_OrcamentoGerado_enfileirado_no_documento()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            Diagnostico(new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)), Cliente, "corr-7", Agora);

        var evento = Assert.Single(orcamento.EventosPendentes);

        Assert.Equal("OrcamentoGerado", evento.Tipo);
        Assert.Equal("corr-7", evento.CorrelationId);
        Assert.Equal(EStatusOutbox.Pendente, evento.Status);
    }

    [Fact]
    public void Diagnostico_sem_item_nao_gera_orcamento()
    {
        Assert.Throws<ArgumentException>(
            () => MontadorDeOrcamento.Montar(Diagnostico(), Cliente, "corr-1", Agora));
    }

    [Fact]
    public void Aprovar_muda_o_status_e_enfileira_o_evento()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            Diagnostico(new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)), Cliente, "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora);

        Assert.Equal(EStatusOrcamento.Aprovado, orcamento.Status);
        Assert.Equal("OrcamentoAprovado", Assert.Single(orcamento.EventosPendentes).Tipo);
    }

    [Fact]
    public void Nao_da_para_aprovar_um_orcamento_ja_respondido()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            Diagnostico(new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)), Cliente, "corr-1", Agora);

        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora);

        Assert.Throws<InvalidOperationException>(() => MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora));
        Assert.Throws<InvalidOperationException>(() => MontadorDeOrcamento.Rejeitar(orcamento, "mudei de ideia", "corr-1", Agora));
    }

    [Fact]
    public void Cancelar_duas_vezes_e_no_op()
    {
        // A compensação chega por evento, e evento chega ao menos uma vez. A
        // segunda entrega não pode virar erro e mandar a mensagem para a dead
        // letter depois do trabalho já ter sido feito.
        var orcamento = MontadorDeOrcamento.Montar(
            Diagnostico(new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)), Cliente, "corr-1", Agora);

        MontadorDeOrcamento.Cancelar(orcamento, "saga desistiu", Agora);
        MontadorDeOrcamento.Cancelar(orcamento, "saga desistiu", Agora);

        Assert.Equal(EStatusOrcamento.Cancelado, orcamento.Status);
    }
}

/// <summary>
/// O Billing contra um MongoDB de verdade.
///
/// O ponto que precisa ser provado aqui é o outbox embutido: gravar o orçamento
/// e enfileirar o evento é UMA escrita de documento, atômica por definição, sem
/// depender de transação multi-documento — que exigiria replica set e não
/// existe no M0 gratuito do Atlas.
/// </summary>
public sealed class BillingMongoTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = InfraDeTeste.Mongo();

    private IMongoDatabase _banco = null!;
    private OrcamentoRepositorio _orcamentos = null!;
    private OutboxMongo _outbox = null!;

    public async Task InitializeAsync()
    {
        await _mongo.StartAsync();

        _banco = new MongoClient(_mongo.GetConnectionString()).GetDatabase("billing-db");
        _orcamentos = new OrcamentoRepositorio(_banco);
        _outbox = new OutboxMongo(_banco);

        await _orcamentos.GarantirIndicesAsync();
    }

    public async Task DisposeAsync() => await _mongo.DisposeAsync();

    private static Orcamento Montar(int idOrdem, string correlationId = "corr-1") =>
        MontadorDeOrcamento.Montar(
            new DiagnosticoConcluido(idOrdem, 7, 10, "Ana Prado", "ana@oficina.test",
                [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m),
                 new ItemDaOrdem("Filtro", ETipoItem.Peca, 2, 38.90m)],
                198.30m, DateTime.UtcNow),
            new DadosDoCliente(10, "Ana Prado", "ana@oficina.test"),
            correlationId,
            DateTime.UtcNow);

    [Fact]
    public async Task Documento_sobrevive_a_ida_e_volta_com_os_decimais_intactos()
    {
        // Decimal em Mongo é onde dinheiro costuma virar double e perder
        // centavo. O mapeamento usa Decimal128 justamente para isso.
        var orcamento = Montar(1);
        await _orcamentos.SalvarAsync(orcamento);

        var lido = await _orcamentos.ObterPorOrdemAsync(1);

        Assert.NotNull(lido);
        Assert.Equal(198.30m, lido.Valor);
        Assert.Equal(77.80m, lido.Itens[1].Subtotal);
        Assert.Equal("Ana Prado", lido.NomeCliente);
        Assert.Equal(EStatusOrcamento.Pendente, lido.Status);
    }

    [Fact]
    public async Task Uma_ordem_nao_pode_ter_dois_orcamentos()
    {
        await _orcamentos.SalvarAsync(Montar(2));

        // O índice único é a rede de proteção. Deixar isso só na disciplina da
        // aplicação foi o que deixou a Fase 3 sem UNIQUE em CPF.
        await Assert.ThrowsAsync<MongoWriteException>(() => _orcamentos.SalvarAsync(Montar(2)));
    }

    [Fact]
    public async Task O_evento_e_gravado_junto_com_o_orcamento_numa_escrita_so()
    {
        var orcamento = Montar(3, "corr-3");
        await _orcamentos.SalvarAsync(orcamento);

        var pendentes = await _outbox.ObterPendentesAsync(10);

        var evento = Assert.Single(pendentes, e => e.CorrelationId == "corr-3");
        Assert.Equal("OrcamentoGerado", evento.Tipo);
        Assert.Equal(RoutingKeys.OrcamentoGerado, evento.RoutingKey);
    }

    [Fact]
    public async Task Publicar_remove_o_evento_do_documento_sem_tocar_no_orcamento()
    {
        var orcamento = Montar(4, "corr-4");
        await _orcamentos.SalvarAsync(orcamento);

        var evento = Assert.Single(orcamento.EventosPendentes);
        await _outbox.MarcarPublicadoAsync(evento.MensagemId, DateTime.UtcNow);

        var lido = await _orcamentos.ObterPorOrdemAsync(4);

        Assert.NotNull(lido);
        Assert.Empty(lido.EventosPendentes);
        Assert.Equal(198.30m, lido.Valor);
        Assert.DoesNotContain(await _outbox.ObterPendentesAsync(10), e => e.MensagemId == evento.MensagemId);
    }

    [Fact]
    public async Task Falha_na_publicacao_agenda_a_proxima_tentativa_sem_perder_o_evento()
    {
        var orcamento = Montar(5, "corr-5");
        await _orcamentos.SalvarAsync(orcamento);
        var evento = Assert.Single(orcamento.EventosPendentes);

        var daqui = DateTime.UtcNow.AddMinutes(5);
        await _outbox.RegistrarFalhaAsync(evento.MensagemId, 1, daqui, "broker fora", EStatusOutbox.Pendente);

        // Agendado para o futuro: não entra no lote agora, mas continua lá.
        Assert.DoesNotContain(await _outbox.ObterPendentesAsync(10), e => e.MensagemId == evento.MensagemId);

        var lido = await _orcamentos.ObterPorOrdemAsync(5);
        var guardado = Assert.Single(lido!.EventosPendentes);

        Assert.Equal(1, guardado.Tentativas);
        Assert.Equal("broker fora", guardado.UltimoErro);
    }

    [Fact]
    public async Task O_processador_do_outbox_funciona_igual_no_Mongo_e_no_SQL()
    {
        // A prova de que a interface vale: o mesmo ProcessadorDoOutbox que roda
        // sobre Azure SQL roda sobre Mongo sem saber onde o dado mora.
        var orcamento = Montar(6, "corr-6");
        await _orcamentos.SalvarAsync(orcamento);

        var publicador = new PublicadorFalso();
        var processador = new ProcessadorDoOutbox(_outbox, publicador);

        var publicados = await processador.ProcessarLoteAsync();

        Assert.Equal(1, publicados);
        Assert.Equal("OrcamentoGerado", Assert.Single(publicador.Publicados).Tipo);

        var lido = await _orcamentos.ObterPorOrdemAsync(6);
        Assert.Empty(lido!.EventosPendentes);
    }

    [Fact]
    public async Task Aprovacao_gravada_enfileira_o_evento_no_mesmo_documento()
    {
        var orcamento = Montar(7, "corr-7");
        await _orcamentos.SalvarAsync(orcamento);
        await _outbox.MarcarPublicadoAsync(orcamento.EventosPendentes[0].MensagemId, DateTime.UtcNow);

        var paraAprovar = await _orcamentos.ObterPorOrdemAsync(7);
        MontadorDeOrcamento.Aprovar(paraAprovar!, "corr-7", DateTime.UtcNow);
        await _orcamentos.SalvarAsync(paraAprovar!);

        var lido = await _orcamentos.ObterPorOrdemAsync(7);

        Assert.Equal(EStatusOrcamento.Aprovado, lido!.Status);
        Assert.Equal("OrcamentoAprovado", Assert.Single(lido.EventosPendentes).Tipo);
    }
}
