using System.Net;
using System.Net.Http.Json;
using MongoDB.Driver;
using PosTech.Contratos;

namespace PosTech.Billing.Testes;

/// <summary>
/// O Billing inteiro, do jeito que roda: evento entra pelo RabbitMQ, orçamento
/// nasce no Mongo, cliente responde por HTTP, e o evento de saída atravessa o
/// broker até a fila do orquestrador.
///
/// Nada aqui é dublê, exceto o adquirente — e ele é dublê porque o Mercado
/// Pago não é parte do nosso sistema. O resto é a infraestrutura de verdade,
/// porque é nela que mora o que dublê não pega: topologia errada, routing key
/// trocada, evento que fica preso no outbox.
/// </summary>
[Collection("billing")]
public class FluxoDoBillingTests
{
    private readonly AmbienteDoBilling _ambiente;

    public FluxoDoBillingTests(AmbienteDoBilling ambiente)
    {
        _ambiente = ambiente;
        _ambiente.Gateway.Reiniciar();
    }

    private static DiagnosticoConcluido Diagnostico(int idOrdem, decimal valor = 198.30m) =>
        new(IdOrdem: idOrdem, IdFuncionarioResponsavel: 7,
            IdCliente: 10, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
            Itens:
            [
                new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m),
                new ItemDaOrdem("Filtro de óleo", ETipoItem.Peca, 2, 38.90m),
            ],
            ValorTotal: valor, ConcluidoEm: DateTime.UtcNow);

    private Task<Orcamento> EsperarOrcamentoAsync(int idOrdem) =>
        InfraDeTeste.AguardarAsync(
            () => _ambiente.OrcamentoDaOrdemAsync(idOrdem),
            $"o orçamento da ordem {idOrdem} aparecer no Mongo");

    [Fact]
    public async Task O_diagnostico_publicado_pelo_Execucao_vira_orcamento_e_volta_como_evento()
    {
        // O caminho completo do passo 2 para o 3 da saga, sem atalho: o evento
        // sai de outro serviço, atravessa o broker, o consumidor do Billing
        // trata, o documento grava, e o publicador do outbox devolve o
        // OrcamentoGerado para a fila do orquestrador.
        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "OrcamentoGerado");

        await _ambiente.PublicarComoAsync(Diagnostico(1001), "execucao-service", "corr-1001");

        var orcamento = await EsperarOrcamentoAsync(1001);

        Assert.Equal(198.30m, orcamento.Valor);
        Assert.Equal("Ana Prado", orcamento.NomeCliente);
        Assert.Equal(EStatusOrcamento.Pendente, orcamento.Status);

        var evento = await espiao.EsperarAsync("OrcamentoGerado", "corr-1001");

        // A correlação sobreviveu à travessia inteira. É o que permite seguir
        // uma ordem pelos quatro serviços num log só.
        Assert.Equal("corr-1001", evento.CorrelationId);

        var gerado = evento.Ler<OrcamentoGerado>();
        Assert.NotNull(gerado);
        Assert.Equal(1001, gerado.IdOrdem);
        Assert.Equal(198.30m, gerado.Valor);
    }

    [Fact]
    public async Task A_mesma_mensagem_entregue_duas_vezes_gera_um_orcamento_so()
    {
        // Entrega repetida é comportamento normal do RabbitMQ, não anomalia. O
        // registro de idempotência é o que separa "o evento chegou duas vezes"
        // de "o cliente recebeu duas cobranças".
        var diagnostico = Diagnostico(1002);

        await _ambiente.PublicarComoAsync(diagnostico, "execucao-service", "corr-1002");
        await EsperarOrcamentoAsync(1002);

        await _ambiente.PublicarComoAsync(diagnostico, "execucao-service", "corr-1002");
        await Task.Delay(2000);

        var quantos = await _ambiente.Banco
            .GetCollection<Orcamento>("orcamentos")
            .CountDocumentsAsync(Builders<Orcamento>.Filter.Eq(o => o.IdOrdem, 1002));

        Assert.Equal(1, quantos);
    }

    [Fact]
    public async Task Aprovar_cobra_o_adquirente_e_publica_a_aprovacao()
    {
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Pendente, string.Empty, null, "https://pagar.test/1003");

        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "OrcamentoAprovado");

        await _ambiente.PublicarComoAsync(Diagnostico(1003), "execucao-service", "corr-1003");
        await EsperarOrcamentoAsync(1003);

        var resposta = await _ambiente.Cliente("corr-1003").PostAsync("/api/v1/orcamentos/os/1003/aprovar", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1003);

        Assert.Equal(EStatusOrcamento.Aprovado, orcamento!.Status);
        Assert.Equal(EStatusDoPagamento.Aguardando, orcamento.StatusDoPagamento);
        Assert.Equal("https://pagar.test/1003", orcamento.UrlDePagamento);
        Assert.Single(_ambiente.Gateway.Cobradas);

        await espiao.EsperarAsync("OrcamentoAprovado", "corr-1003");
    }

    [Fact]
    public async Task Aprovar_duas_vezes_nao_cobra_duas_vezes()
    {
        await _ambiente.PublicarComoAsync(Diagnostico(1004), "execucao-service", "corr-1004");
        await EsperarOrcamentoAsync(1004);

        var cliente = _ambiente.Cliente();

        var primeira = await cliente.PostAsync("/api/v1/orcamentos/os/1004/aprovar", null);
        var segunda = await cliente.PostAsync("/api/v1/orcamentos/os/1004/aprovar", null);

        Assert.Equal(HttpStatusCode.OK, primeira.StatusCode);

        // 409 e não 200: o segundo clique não é sucesso silencioso, é conflito.
        // Devolver 200 esconderia do cliente que nada aconteceu.
        Assert.Equal(HttpStatusCode.Conflict, segunda.StatusCode);
        Assert.Single(_ambiente.Gateway.Cobradas);
    }

    [Fact]
    public async Task Sandbox_que_ja_responde_aprovado_confirma_o_pagamento_sem_webhook()
    {
        // Em sandbox a cobrança pode voltar aprovada na hora. Depender do
        // webhook para esse caso deixaria a demonstração presa numa notificação
        // que pode não vir.
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Aprovada, string.Empty, null, null);

        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "PagamentoConfirmado");

        await _ambiente.PublicarComoAsync(Diagnostico(1005), "execucao-service", "corr-1005");
        await EsperarOrcamentoAsync(1005);

        await _ambiente.Cliente("corr-1005").PostAsync("/api/v1/orcamentos/os/1005/aprovar", null);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1005);
        Assert.Equal(EStatusDoPagamento.Confirmado, orcamento!.StatusDoPagamento);

        var evento = await espiao.EsperarAsync("PagamentoConfirmado", "corr-1005");
        var confirmado = evento.Ler<PagamentoConfirmado>();

        Assert.NotNull(confirmado);
        Assert.Equal(1005, confirmado.IdOrdem);
    }

    [Fact]
    public async Task Cobranca_recusada_pelo_adquirente_publica_a_recusa_e_nao_cancela_o_orcamento()
    {
        // Recusa volta um passo, não cancela: o cliente pode tentar outro meio
        // de pagamento e o orçamento continua valendo. É a regra do ADR-0006.
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Recusada, string.Empty, "Cartão sem limite", null);

        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "PagamentoRecusado");

        await _ambiente.PublicarComoAsync(Diagnostico(1006), "execucao-service", "corr-1006");
        await EsperarOrcamentoAsync(1006);

        await _ambiente.Cliente("corr-1006").PostAsync("/api/v1/orcamentos/os/1006/aprovar", null);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1006);

        Assert.Equal(EStatusDoPagamento.Recusado, orcamento!.StatusDoPagamento);
        Assert.Equal(EStatusOrcamento.Aprovado, orcamento.Status);
        Assert.Equal("Cartão sem limite", orcamento.MotivoDaRecusaDoPagamento);

        await espiao.EsperarAsync("PagamentoRecusado", "corr-1006");
    }

    [Fact]
    public async Task Adquirente_fora_do_ar_devolve_502_sem_desfazer_a_aprovacao()
    {
        // A aprovação já valeu e o OrcamentoAprovado já saiu. Desfazer por
        // causa da cobrança seria mentir sobre uma decisão que o cliente tomou.
        _ambiente.Gateway.Falha = new HttpRequestException("adquirente fora do ar");

        await _ambiente.PublicarComoAsync(Diagnostico(1007), "execucao-service", "corr-1007");
        await EsperarOrcamentoAsync(1007);

        var resposta = await _ambiente.Cliente().PostAsync("/api/v1/orcamentos/os/1007/aprovar", null);

        Assert.Equal(HttpStatusCode.BadGateway, resposta.StatusCode);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1007);

        Assert.Equal(EStatusOrcamento.Aprovado, orcamento!.Status);
        Assert.Equal(EStatusDoPagamento.SemCobranca, orcamento.StatusDoPagamento);
    }

    [Fact]
    public async Task Rejeitar_publica_a_rejeicao_com_o_motivo_do_cliente()
    {
        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "OrcamentoRejeitado");

        await _ambiente.PublicarComoAsync(Diagnostico(1008), "execucao-service", "corr-1008");
        await EsperarOrcamentoAsync(1008);

        var resposta = await _ambiente.Cliente("corr-1008").PostAsJsonAsync(
            "/api/v1/orcamentos/os/1008/rejeitar", new { motivo = "Vou fazer em outro lugar" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var evento = await espiao.EsperarAsync("OrcamentoRejeitado", "corr-1008");
        var rejeitado = evento.Ler<OrcamentoRejeitado>();

        Assert.NotNull(rejeitado);
        Assert.Equal("Vou fazer em outro lugar", rejeitado.Motivo);
    }

    [Fact]
    public async Task Rejeitar_o_que_ja_foi_aprovado_e_conflito()
    {
        await _ambiente.PublicarComoAsync(Diagnostico(1009), "execucao-service", "corr-1009");
        await EsperarOrcamentoAsync(1009);

        var cliente = _ambiente.Cliente();

        await cliente.PostAsync("/api/v1/orcamentos/os/1009/aprovar", null);

        var resposta = await cliente.PostAsJsonAsync(
            "/api/v1/orcamentos/os/1009/rejeitar", new { motivo = "mudei de ideia" });

        Assert.Equal(HttpStatusCode.Conflict, resposta.StatusCode);
    }

    [Fact]
    public async Task O_cancelamento_da_ordem_depois_do_pagamento_estorna_e_avisa()
    {
        // A compensação que fecha a saga: o OS Service cancelou a ordem, e o
        // Billing precisa devolver o dinheiro e dizer que devolveu — é esse
        // PagamentoEstornado que tira a saga de Compensando.
        _ambiente.Gateway.Resposta = new Cobranca(EResultadoDaCobranca.Aprovada, string.Empty, null, null);

        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "PagamentoEstornado");

        await _ambiente.PublicarComoAsync(Diagnostico(1010), "execucao-service", "corr-1010");
        await EsperarOrcamentoAsync(1010);

        await _ambiente.Cliente("corr-1010").PostAsync("/api/v1/orcamentos/os/1010/aprovar", null);

        await _ambiente.PublicarComoAsync(
            new OrdemCancelada(1010, "execução não assumiu no prazo", DateTime.UtcNow),
            "os-service",
            "corr-1010");

        var evento = await espiao.EsperarAsync("PagamentoEstornado", "corr-1010");
        var estornado = evento.Ler<PagamentoEstornado>();

        Assert.NotNull(estornado);
        Assert.Equal(1010, estornado.IdOrdem);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1010);

        Assert.Equal(EStatusDoPagamento.Estornado, orcamento!.StatusDoPagamento);
        Assert.Equal(EStatusOrcamento.Cancelado, orcamento.Status);
    }

    [Fact]
    public async Task O_webhook_confirma_consultando_o_adquirente_e_nao_acreditando_na_notificacao()
    {
        // A rota do webhook é pública. O teste manda uma notificação dizendo
        // apenas "o pagamento mp-1011 mudou" — sem status nenhum no corpo — e
        // mesmo assim o pagamento é confirmado, porque quem responde é a
        // consulta de volta ao adquirente.
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Pendente, "mp-1011", null, "https://pagar.test/1011");

        await using var espiao = await EspiaoDaFila.LigarAsync(
            _ambiente.RabbitConnectionString, Topologia.FilaOrdemServico, "PagamentoConfirmado");

        await _ambiente.PublicarComoAsync(Diagnostico(1011), "execucao-service", "corr-1011");
        await EsperarOrcamentoAsync(1011);

        var cliente = _ambiente.Cliente("corr-1011");
        await cliente.PostAsync("/api/v1/orcamentos/os/1011/aprovar", null);

        // Agora o adquirente passa a dizer que aprovou.
        _ambiente.Gateway.Resposta = new Cobranca(EResultadoDaCobranca.Aprovada, "mp-1011", null, null);

        var resposta = await cliente.PostAsJsonAsync(
            "/api/v1/pagamentos/webhook", new { type = "payment", data = new { id = "mp-1011" } });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1011);
        Assert.Equal(EStatusDoPagamento.Confirmado, orcamento!.StatusDoPagamento);

        await espiao.EsperarAsync("PagamentoConfirmado", "corr-1011");
    }

    [Fact]
    public async Task O_mesmo_webhook_entregue_duas_vezes_confirma_uma_vez_so()
    {
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Pendente, "mp-1012", null, "https://pagar.test/1012");

        await _ambiente.PublicarComoAsync(Diagnostico(1012), "execucao-service", "corr-1012");
        await EsperarOrcamentoAsync(1012);

        var cliente = _ambiente.Cliente();
        await cliente.PostAsync("/api/v1/orcamentos/os/1012/aprovar", null);

        _ambiente.Gateway.Resposta = new Cobranca(EResultadoDaCobranca.Aprovada, "mp-1012", null, null);

        var corpo = new { type = "payment", data = new { id = "mp-1012" } };

        await cliente.PostAsJsonAsync("/api/v1/pagamentos/webhook", corpo);
        var segunda = await cliente.PostAsJsonAsync("/api/v1/pagamentos/webhook", corpo);

        // O Mercado Pago reenvia até receber 2xx; a segunda entrega responde
        // 200 dizendo que não aplicou nada, e não gera um segundo evento.
        Assert.Equal(HttpStatusCode.OK, segunda.StatusCode);

        var orcamento = await _ambiente.OrcamentoDaOrdemAsync(1012);

        Assert.Equal(EStatusDoPagamento.Confirmado, orcamento!.StatusDoPagamento);
        Assert.DoesNotContain(
            orcamento.EventosPendentes.Where(e => e.Tipo == "PagamentoConfirmado").Skip(1), _ => true);
    }

    [Fact]
    public async Task A_consulta_do_orcamento_pela_API_devolve_os_itens_com_subtotal()
    {
        await _ambiente.PublicarComoAsync(Diagnostico(1013), "execucao-service", "corr-1013");
        await EsperarOrcamentoAsync(1013);

        var resposta = await _ambiente.Cliente().GetAsync("/api/v1/orcamentos/os/1013");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);

        var corpo = await resposta.Content.ReadFromJsonAsync<RespostaDeOrcamento>();

        Assert.NotNull(corpo);
        Assert.Equal(198.30m, corpo.Valor);
        Assert.Equal("Pendente", corpo.Status);
        Assert.Equal(2, corpo.Itens.Count);
        Assert.Equal(77.80m, corpo.Itens[1].Subtotal);
    }

    private sealed record RespostaDeOrcamento(
        string Id, int IdOrdem, string NomeCliente, decimal Valor, string Status,
        string StatusDoPagamento, IReadOnlyList<ItemDaResposta> Itens);

    private sealed record ItemDaResposta(
        string Descricao, string Tipo, int Quantidade, decimal ValorUnitario, decimal Subtotal);
}
