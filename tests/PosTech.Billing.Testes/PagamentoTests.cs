using System.Net;
using System.Text;
using PosTech.Billing;
using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing.Testes;

/// <summary>
/// As regras do dinheiro. São as que mais doem quando erram: confirmar duas
/// vezes libera a oficina em dobro, estornar o que não foi pago inventa
/// movimento financeiro, e tratar status desconhecido como recusa cancela
/// ordem paga.
/// </summary>
public class PagamentoDoOrcamentoTests
{
    private static readonly DateTime Agora = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    private static Orcamento Aprovado()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            new DiagnosticoConcluido(1, 7, 10, "Ana Prado", "ana@oficina.test",
                [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 198.30m)], 198.30m, Agora),
            new DadosDoCliente(10, "Ana Prado", "ana@oficina.test"),
            "corr-1", Agora);

        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        return orcamento;
    }

    private static Cobranca CobrancaAprovada(string referencia = "mp-9988") =>
        new(EResultadoDaCobranca.Aprovada, referencia, null, null);

    // -----------------------------------------------------------------------
    // Criar cobrança
    // -----------------------------------------------------------------------

    [Fact]
    public void Registrar_a_cobranca_nao_libera_a_oficina()
    {
        // Criar cobrança não é pagamento. Publicar PagamentoConfirmado aqui
        // mandaria a oficina trabalhar antes de o dinheiro entrar.
        var orcamento = Aprovado();

        PagamentoDoOrcamento.RegistrarCobranca(
            orcamento, new Cobranca(EResultadoDaCobranca.Pendente, "mp-1", null, "https://pagar"), Agora);

        Assert.Equal("mp-1", orcamento.ReferenciaExterna);
        Assert.Equal("https://pagar", orcamento.UrlDePagamento);
        Assert.Equal(EStatusDoPagamento.SemCobranca, orcamento.StatusDoPagamento);
        Assert.Empty(orcamento.EventosPendentes);
    }

    [Fact]
    public void Orcamento_nao_aprovado_nao_pode_ser_cobrado()
    {
        var pendente = MontadorDeOrcamento.Montar(
            new DiagnosticoConcluido(1, 7, 10, "Ana", "a@a.test", [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 100m)], 100m, Agora),
            new DadosDoCliente(10, "Ana", "a@a.test"), "corr-1", Agora);

        Assert.Throws<InvalidOperationException>(
            () => PagamentoDoOrcamento.RegistrarCobranca(pendente, CobrancaAprovada(), Agora));
    }

    // -----------------------------------------------------------------------
    // Confirmar
    // -----------------------------------------------------------------------

    [Fact]
    public void Confirmar_publica_PagamentoConfirmado_com_a_referencia_do_adquirente()
    {
        var orcamento = Aprovado();

        var mudou = PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora);

        Assert.True(mudou);
        Assert.Equal(EStatusDoPagamento.Confirmado, orcamento.StatusDoPagamento);
        Assert.Equal(Agora, orcamento.PagoEm);

        var evento = Assert.Single(orcamento.EventosPendentes);
        Assert.Equal("PagamentoConfirmado", evento.Tipo);

        var dados = SerializacaoDeEvento.Desserializar<PagamentoConfirmado>(evento.Payload)!.Dados;
        Assert.Equal("mp-9988", dados.ReferenciaExterna);
        Assert.Equal(198.30m, dados.Valor);
    }

    [Fact]
    public void Confirmar_duas_vezes_publica_um_evento_so()
    {
        // A notificação do adquirente chega repetida. Dois
        // PagamentoConfirmado seriam ignorados pelo orquestrador, mas depender
        // disso seria terceirizar a correção para o outro serviço.
        var orcamento = Aprovado();

        Assert.True(PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora));
        Assert.False(PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora));

        Assert.Single(orcamento.EventosPendentes);
    }

    [Fact]
    public void Confirmacao_para_orcamento_cancelado_estoura_em_vez_de_passar_batido()
    {
        // Dinheiro que entrou numa ordem cancelada precisa de estorno. Aceitar
        // em silêncio deixaria o cliente pago e sem serviço.
        var orcamento = Aprovado();
        MontadorDeOrcamento.Cancelar(orcamento, "saga desistiu", Agora);

        var erro = Assert.Throws<InvalidOperationException>(
            () => PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora));

        Assert.Contains("estorno", erro.Message);
    }

    // -----------------------------------------------------------------------
    // Recusar
    // -----------------------------------------------------------------------

    [Fact]
    public void Recusa_nao_cancela_o_orcamento()
    {
        // O cliente pode tentar outro meio. O orçamento continua valendo e a
        // saga volta um passo em vez de desistir.
        var orcamento = Aprovado();

        Assert.True(PagamentoDoOrcamento.Recusar(orcamento, "Cartão sem limite", "corr-1", Agora));

        Assert.Equal(EStatusDoPagamento.Recusado, orcamento.StatusDoPagamento);
        Assert.Equal(EStatusOrcamento.Aprovado, orcamento.Status);
        Assert.Equal("PagamentoRecusado", Assert.Single(orcamento.EventosPendentes).Tipo);
    }

    [Fact]
    public void Recusa_depois_de_confirmado_nao_desfaz_o_pagamento()
    {
        // Notificação atrasada do adquirente. Aceitar a recusa aqui tiraria do
        // ar uma ordem que foi paga e já pode estar sendo executada.
        var orcamento = Aprovado();
        PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        Assert.False(PagamentoDoOrcamento.Recusar(orcamento, "atrasada", "corr-1", Agora));

        Assert.Equal(EStatusDoPagamento.Confirmado, orcamento.StatusDoPagamento);
        Assert.Empty(orcamento.EventosPendentes);
    }

    // -----------------------------------------------------------------------
    // Estornar
    // -----------------------------------------------------------------------

    [Fact]
    public void Estornar_o_que_foi_pago_publica_PagamentoEstornado()
    {
        var orcamento = Aprovado();
        PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        Assert.True(PagamentoDoOrcamento.Estornar(orcamento, "execução não assumiu", "corr-1", Agora));

        Assert.Equal(EStatusDoPagamento.Estornado, orcamento.StatusDoPagamento);

        var dados = SerializacaoDeEvento
            .Desserializar<PagamentoEstornado>(Assert.Single(orcamento.EventosPendentes).Payload)!.Dados;

        Assert.Equal("mp-9988", dados.ReferenciaExterna);
        Assert.Equal(198.30m, dados.Valor);
        Assert.Equal("execução não assumiu", dados.Motivo);
    }

    [Fact]
    public void Estornar_o_que_nunca_foi_pago_nao_inventa_movimento_financeiro()
    {
        // A mesma regra do orquestrador: compensar só o que aconteceu.
        var orcamento = Aprovado();

        Assert.False(PagamentoDoOrcamento.Estornar(orcamento, "cancelada", "corr-1", Agora));

        Assert.Empty(orcamento.EventosPendentes);
        Assert.Equal(EStatusDoPagamento.SemCobranca, orcamento.StatusDoPagamento);
    }

    [Fact]
    public void Estornar_duas_vezes_publica_um_evento_so()
    {
        var orcamento = Aprovado();
        PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora);
        orcamento.EventosPendentes.Clear();

        Assert.True(PagamentoDoOrcamento.Estornar(orcamento, "cancelada", "corr-1", Agora));
        Assert.False(PagamentoDoOrcamento.Estornar(orcamento, "cancelada", "corr-1", Agora));

        Assert.Single(orcamento.EventosPendentes);
    }

    [Fact]
    public void O_id_do_pagamento_e_o_mesmo_na_confirmacao_e_no_estorno()
    {
        // Derivado do orçamento, não sorteado: os dois eventos falam do mesmo
        // pagamento mesmo publicados em momentos diferentes.
        var orcamento = Aprovado();

        PagamentoDoOrcamento.Confirmar(orcamento, CobrancaAprovada(), "corr-1", Agora);
        var confirmado = SerializacaoDeEvento
            .Desserializar<PagamentoConfirmado>(orcamento.EventosPendentes[0].Payload)!.Dados.IdPagamento;

        PagamentoDoOrcamento.Estornar(orcamento, "cancelada", "corr-1", Agora);
        var estornado = SerializacaoDeEvento
            .Desserializar<PagamentoEstornado>(orcamento.EventosPendentes[1].Payload)!.Dados.IdPagamento;

        Assert.Equal(confirmado, estornado);
    }
}

/// <summary>
/// A tradução do vocabulário do Mercado Pago, exercitada sem rede.
/// </summary>
public class GatewayMercadoPagoTests
{
    private static HttpClient ClienteQueResponde(HttpStatusCode codigo, string json) =>
        new(new RespostaFixa(codigo, json)) { BaseAddress = new Uri("https://api.mercadopago.com") };

    private static Orcamento UmOrcamento()
    {
        var orcamento = MontadorDeOrcamento.Montar(
            new DiagnosticoConcluido(1, 7, 10, "Ana", "ana@oficina.test", [new ItemDaOrdem("Troca", ETipoItem.MaoDeObra, 1, 198.30m)], 198.30m, DateTime.UtcNow),
            new DadosDoCliente(10, "Ana", "ana@oficina.test"), "corr-1", DateTime.UtcNow);

        MontadorDeOrcamento.Aprovar(orcamento, "corr-1", DateTime.UtcNow);

        return orcamento;
    }

    [Fact]
    public async Task Cobranca_aprovada_vira_resultado_aprovado_com_a_referencia()
    {
        var http = ClienteQueResponde(HttpStatusCode.Created, """
            {"id": 123456789, "status": "approved", "status_detail": "accredited"}
            """);

        var cobranca = await new GatewayMercadoPago(http, "token-teste")
            .CriarCobrancaAsync(UmOrcamento(), "corr-1");

        Assert.Equal(EResultadoDaCobranca.Aprovada, cobranca.Resultado);
        Assert.Equal("123456789", cobranca.ReferenciaExterna);
        Assert.Null(cobranca.MotivoDaRecusa);
    }

    [Fact]
    public async Task Cobranca_recusada_traz_o_motivo_do_adquirente()
    {
        var http = ClienteQueResponde(HttpStatusCode.Created, """
            {"id": 987, "status": "rejected", "status_detail": "cc_rejected_insufficient_amount"}
            """);

        var cobranca = await new GatewayMercadoPago(http, "token-teste")
            .CriarCobrancaAsync(UmOrcamento(), "corr-1");

        Assert.Equal(EResultadoDaCobranca.Recusada, cobranca.Resultado);
        Assert.Equal("cc_rejected_insufficient_amount", cobranca.MotivoDaRecusa);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("in_process")]
    [InlineData("authorized")]
    [InlineData("um_status_que_ainda_nao_existe")]
    public async Task Status_que_nao_e_aprovado_nem_recusado_fica_pendente(string status)
    {
        // Tratar desconhecido como recusa cancelaria ordem paga. O Mercado Pago
        // já introduziu status novo antes, e vai introduzir de novo.
        var http = ClienteQueResponde(HttpStatusCode.OK, $$"""
            {"id": 1, "status": "{{status}}"}
            """);

        var cobranca = await new GatewayMercadoPago(http, "token-teste").ConsultarAsync("1");

        Assert.Equal(EResultadoDaCobranca.Pendente, cobranca.Resultado);
    }

    [Fact]
    public async Task Pix_devolve_o_link_de_pagamento()
    {
        var http = ClienteQueResponde(HttpStatusCode.Created, """
            {"id": 55, "status": "pending",
             "point_of_interaction": {"transaction_data": {"ticket_url": "https://mp/pix/55"}}}
            """);

        var cobranca = await new GatewayMercadoPago(http, "token-teste")
            .CriarCobrancaAsync(UmOrcamento(), "corr-1");

        Assert.Equal("https://mp/pix/55", cobranca.UrlDePagamento);
    }

    [Fact]
    public async Task Erro_do_adquirente_estoura_com_o_corpo_no_texto()
    {
        // Precisa estourar, e não devolver pendente em silêncio: o outbox
        // reagenda e tenta de novo, e uma falha engolida viraria uma ordem
        // travada esperando um pagamento que nunca foi criado.
        var http = ClienteQueResponde(HttpStatusCode.BadRequest, """{"message": "invalid payer email"}""");

        var erro = await Assert.ThrowsAsync<HttpRequestException>(
            () => new GatewayMercadoPago(http, "token-teste").CriarCobrancaAsync(UmOrcamento(), "corr-1"));

        Assert.Contains("400", erro.Message);
        Assert.Contains("invalid payer email", erro.Message);
    }

    [Fact]
    public async Task A_requisicao_leva_chave_de_idempotencia_e_correlation()
    {
        // Sem a chave de idempotência, um timeout de rede e o retry seguinte
        // criariam duas cobranças para o mesmo orçamento.
        var espiao = new RespostaFixa(HttpStatusCode.Created, """{"id": 1, "status": "approved"}""");
        var http = new HttpClient(espiao) { BaseAddress = new Uri("https://api.mercadopago.com") };

        var orcamento = UmOrcamento();
        await new GatewayMercadoPago(http, "token-teste").CriarCobrancaAsync(orcamento, "corr-42");

        Assert.Equal(orcamento.Id, espiao.UltimaRequisicao!.Headers.GetValues("X-Idempotency-Key").Single());
        Assert.Equal("corr-42", espiao.UltimaRequisicao.Headers.GetValues("X-Correlation-ID").Single());
        Assert.Equal("Bearer", espiao.UltimaRequisicao.Headers.Authorization!.Scheme);
    }

    [Fact]
    public void Gateway_sem_token_nao_sobe()
    {
        Assert.Throws<ArgumentException>(() => new GatewayMercadoPago(new HttpClient(), "  "));
    }

    private sealed class RespostaFixa(HttpStatusCode codigo, string json) : HttpMessageHandler
    {
        public HttpRequestMessage? UltimaRequisicao { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaRequisicao = request;

            return Task.FromResult(new HttpResponseMessage(codigo)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
