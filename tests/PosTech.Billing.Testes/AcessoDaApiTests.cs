using System.Net;
using System.Net.Http.Json;
using PosTech.Billing.Api.Infra;
using PosTech.Contratos;

namespace PosTech.Billing.Testes;

/// <summary>
/// Quem pode o quê nas rotas do Billing.
///
/// Aqui a falta de checagem custava dinheiro: aprovar é o que cria a cobrança,
/// e rejeitar cancela a ordem. Um cliente autenticado fazia as duas coisas no
/// orçamento de qualquer outro, só trocando o número da ordem na rota.
/// </summary>
[Collection("billing")]
public class AcessoDaApiTests
{
    private const int Dono = 10;
    private const int Outro = 11;

    private readonly AmbienteDoBilling _ambiente;

    public AcessoDaApiTests(AmbienteDoBilling ambiente)
    {
        _ambiente = ambiente;
        _ambiente.Gateway.Reiniciar();
    }

    private HttpClient Como(string? token) => _ambiente.Cliente().Como(token);

    private async Task CriarOrcamentoAsync(int idOrdem)
    {
        var diagnostico = new DiagnosticoConcluido(
            IdOrdem: idOrdem, IdFuncionarioResponsavel: 7,
            IdCliente: Dono, NomeCliente: "Ana Prado", EmailCliente: "ana@oficina.test",
            Itens: [new ItemDaOrdem("Troca de óleo", ETipoItem.MaoDeObra, 1, 120.50m)],
            ValorTotal: 120.50m, ConcluidoEm: DateTime.UtcNow);

        await _ambiente.PublicarComoAsync(diagnostico, "execucao-service", $"corr-{idOrdem}");

        await InfraDeTeste.AguardarAsync(
            () => _ambiente.OrcamentoDaOrdemAsync(idOrdem),
            $"o orçamento da ordem {idOrdem} aparecer no Mongo");
    }

    [Fact]
    public async Task Sem_token_o_orcamento_nao_e_lido_aprovado_nem_rejeitado()
    {
        await CriarOrcamentoAsync(7101);

        foreach (var http in new[] { Como(null), Como(TokenDeTeste.Vencido()) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/v1/orcamentos/os/7101")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.PostAsync("/api/v1/orcamentos/os/7101/aprovar", null)).StatusCode);
            Assert.Equal(
                HttpStatusCode.Unauthorized,
                (await http.PostAsJsonAsync("/api/v1/orcamentos/os/7101/rejeitar", new { motivo = "x" })).StatusCode);
        }

        Assert.Equal(EStatusOrcamento.Pendente, (await _ambiente.OrcamentoDaOrdemAsync(7101))!.Status);
    }

    [Fact]
    public async Task O_webhook_e_o_health_continuam_sem_token()
    {
        // Quem chama o webhook é o Mercado Pago, que não tem o nosso token.
        var webhook = await Como(null).PostAsJsonAsync(
            "/api/v1/pagamentos/webhook", new { type = "order", data = new { id = "ORDNAOEXISTE" } });

        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Como(null).GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Outro_cliente_nao_le_o_orcamento_e_o_dono_e_a_equipe_leem()
    {
        await CriarOrcamentoAsync(7102);

        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Cliente(Dono)).GetAsync("/api/v1/orcamentos/os/7102")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Como(TokenDeTeste.Mecanico()).GetAsync("/api/v1/orcamentos/os/7102")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Como(TokenDeTeste.Cliente(Outro)).GetAsync("/api/v1/orcamentos/os/7102")).StatusCode);
    }

    [Fact]
    public async Task Outro_cliente_nao_aprova_e_nada_e_cobrado()
    {
        await CriarOrcamentoAsync(7103);

        var resposta = await Como(TokenDeTeste.Cliente(Outro)).PostAsync("/api/v1/orcamentos/os/7103/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(EStatusOrcamento.Pendente, (await _ambiente.OrcamentoDaOrdemAsync(7103))!.Status);
        Assert.Empty(_ambiente.Gateway.Cobradas);
    }

    [Fact]
    public async Task Outro_cliente_nao_rejeita_e_o_orcamento_continua_pendente()
    {
        await CriarOrcamentoAsync(7104);

        var resposta = await Como(TokenDeTeste.Cliente(Outro))
            .PostAsJsonAsync("/api/v1/orcamentos/os/7104/rejeitar", new { motivo = "não é meu" });

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
        Assert.Equal(EStatusOrcamento.Pendente, (await _ambiente.OrcamentoDaOrdemAsync(7104))!.Status);
    }

    [Fact]
    public async Task O_dono_aprova_o_proprio_orcamento()
    {
        _ambiente.Gateway.Resposta = new Cobranca(
            EResultadoDaCobranca.Pendente, string.Empty, null, "https://pagar.test/7105");

        await CriarOrcamentoAsync(7105);

        var resposta = await Como(TokenDeTeste.Cliente(Dono)).PostAsync("/api/v1/orcamentos/os/7105/aprovar", null);

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.Equal(EStatusOrcamento.Aprovado, (await _ambiente.OrcamentoDaOrdemAsync(7105))!.Status);
    }

    [Fact]
    public async Task O_dono_rejeita_o_proprio_orcamento()
    {
        await CriarOrcamentoAsync(7106);

        var resposta = await Como(TokenDeTeste.Cliente(Dono))
            .PostAsJsonAsync("/api/v1/orcamentos/os/7106/rejeitar", new { motivo = "Achou caro" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
        Assert.NotEqual(EStatusOrcamento.Pendente, (await _ambiente.OrcamentoDaOrdemAsync(7106))!.Status);
    }
}
