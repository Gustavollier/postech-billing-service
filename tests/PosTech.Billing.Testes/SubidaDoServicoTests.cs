using System.Net;
using System.Net.Http.Json;
using MongoDB.Driver;
using PosTech.Billing;
using Testcontainers.MongoDb;

namespace PosTech.Billing.Testes;

/// <summary>
/// A API sobe, e sobe inteira.
///
/// Este arquivo existe por causa de um defeito real: o serviço compilava, 35
/// testes passavam, e ele **não subia** — <c>IRegistroDeIdempotencia</c> nunca
/// tinha sido registrado no contêiner de injeção, e o consumidor morria na
/// partida. Nenhum teste de unidade pega isso, porque nenhum deles monta o
/// contêiner.
///
/// A lição que fica no repositório: montar o grafo de dependências é parte do
/// comportamento do serviço, e tem que ser exercitada como tal.
/// </summary>
[Collection("billing")]
public class SubidaDoServicoTests
{
    private readonly AmbienteDoBilling _ambiente;

    public SubidaDoServicoTests(AmbienteDoBilling ambiente) => _ambiente = ambiente;

    [Fact]
    public async Task O_servico_sobe_com_todas_as_dependencias_resolvidas()
    {
        // O GET é quase incidental: o que se prova aqui é que o host iniciou,
        // o que inclui construir ConsumidorDoBilling, PublicadorDoOutbox e
        // CriadorDeIndices com tudo o que eles pedem no construtor.
        var resposta = await _ambiente.Cliente().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_readiness_responde_separado_do_liveness()
    {
        // São sondas diferentes no manifesto por um motivo: o pod sobe antes de
        // o consumidor conectar no broker, e mandar tráfego nesse intervalo dá
        // erro à toa.
        var resposta = await _ambiente.Cliente().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Orcamento_que_nao_existe_devolve_404_e_nao_500()
    {
        // A rota passa por Mongo de verdade e pela serialização de verdade. É o
        // tipo de rota onde um mapeamento errado só aparece quando se chama.
        var resposta = await _ambiente.Cliente().GetAsync("/api/v1/orcamentos/os/4242");

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task Aprovar_uma_ordem_que_nao_tem_orcamento_devolve_404()
    {
        var resposta = await _ambiente.Cliente().PostAsync("/api/v1/orcamentos/os/4243/aprovar", null);

        Assert.Equal(HttpStatusCode.NotFound, resposta.StatusCode);
    }

    [Fact]
    public async Task O_webhook_responde_200_para_notificacao_que_nao_e_nossa()
    {
        // O Mercado Pago reenvia enquanto não receber 2xx. Devolver erro para
        // uma notificação de outro ambiente viraria tempestade de reenvio.
        var resposta = await _ambiente.Cliente().PostAsJsonAsync(
            "/api/v1/pagamentos/webhook", new { type = "payment", data = new { id = "de-outro-ambiente" } });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task O_webhook_sem_id_no_corpo_tambem_responde_200()
    {
        var resposta = await _ambiente.Cliente().PostAsJsonAsync(
            "/api/v1/pagamentos/webhook", new { type = "payment" });

        Assert.Equal(HttpStatusCode.OK, resposta.StatusCode);
    }

    [Fact]
    public async Task Os_indices_sao_criados_na_partida()
    {
        // O CriadorDeIndices roda como IHostedService. Se ele falhar, o serviço
        // sobe assim mesmo (de propósito) — então o índice único, que é o que
        // impede dois orçamentos para a mesma ordem, precisa ser conferido.
        var indices = await (await _ambiente.Banco
            .GetCollection<Orcamento>("orcamentos").Indexes.ListAsync()).ToListAsync();

        var nomes = indices.Select(i => i["name"].AsString).ToList();

        Assert.Contains("ux_idOrdem", nomes);
        Assert.Contains("ix_referenciaExterna", nomes);
        Assert.Contains("ix_eventosPendentes", nomes);
    }

    [Fact]
    public async Task O_TTL_da_idempotencia_tambem_e_criado_na_partida()
    {
        var indices = await (await _ambiente.Banco
            .GetCollection<EventoProcessado>("eventosProcessados").Indexes.ListAsync()).ToListAsync();

        Assert.Contains(indices, i => i["name"].AsString == "ttl_processadoEm");
    }
}

/// <summary>
/// O registro de idempotência contra Mongo de verdade.
/// </summary>
public sealed class IdempotenciaMongoTests : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = InfraDeTeste.Mongo();

    private IdempotenciaMongo _idempotencia = null!;
    private IMongoDatabase _banco = null!;

    public async Task InitializeAsync()
    {
        await _mongo.StartAsync();

        _banco = new MongoClient(_mongo.GetConnectionString()).GetDatabase("billing-db");
        _idempotencia = new IdempotenciaMongo(_banco);

        await _idempotencia.GarantirIndicesAsync();
    }

    public async Task DisposeAsync() => await _mongo.DisposeAsync();

    [Fact]
    public async Task Mensagem_nova_nao_esta_registrada()
    {
        Assert.False(await _idempotencia.JaProcessadoAsync(Guid.NewGuid(), "billing-service"));
    }

    [Fact]
    public async Task Registrar_marca_a_mensagem_para_aquele_consumidor()
    {
        var mensagem = Guid.NewGuid();

        await _idempotencia.RegistrarAsync(mensagem, "DiagnosticoConcluido", "billing-service", DateTime.UtcNow);

        Assert.True(await _idempotencia.JaProcessadoAsync(mensagem, "billing-service"));
    }

    [Fact]
    public async Task O_mesmo_evento_pode_ser_processado_por_consumidores_diferentes()
    {
        // A chave é (mensagem, consumidor). O mesmo DiagnosticoConcluido chega
        // ao Billing e ao Execução, e um não pode calar o outro.
        var mensagem = Guid.NewGuid();

        await _idempotencia.RegistrarAsync(mensagem, "DiagnosticoConcluido", "billing-service", DateTime.UtcNow);

        Assert.False(await _idempotencia.JaProcessadoAsync(mensagem, "execucao-service"));
    }

    [Fact]
    public async Task Registrar_duas_vezes_nao_estoura()
    {
        // Duas réplicas podem passar pela checagem juntas. Uma violação de
        // chave aqui mandaria para a dead letter uma mensagem já processada com
        // sucesso — o pior resultado possível.
        var mensagem = Guid.NewGuid();

        await _idempotencia.RegistrarAsync(mensagem, "DiagnosticoConcluido", "billing-service", DateTime.UtcNow);
        await _idempotencia.RegistrarAsync(mensagem, "DiagnosticoConcluido", "billing-service", DateTime.UtcNow);

        var registros = await _banco.GetCollection<EventoProcessado>("eventosProcessados")
            .CountDocumentsAsync(FilterDefinition<EventoProcessado>.Empty);

        Assert.Equal(1, registros);
    }

    [Fact]
    public async Task Gravacoes_concorrentes_da_mesma_mensagem_nao_derrubam_o_consumidor()
    {
        var mensagem = Guid.NewGuid();

        var corridas = Enumerable.Range(0, 8).Select(_ =>
            _idempotencia.RegistrarAsync(mensagem, "DiagnosticoConcluido", "billing-service", DateTime.UtcNow));

        // O que importa é não lançar: oito tentativas simultâneas da mesma
        // chave é exatamente o que duas réplicas fazem sob carga.
        await Task.WhenAll(corridas);

        Assert.True(await _idempotencia.JaProcessadoAsync(mensagem, "billing-service"));
    }

    [Fact]
    public async Task O_registro_tem_prazo_de_validade()
    {
        // Sem TTL a coleção cresce para sempre. Trinta dias é muito além de
        // qualquer janela de reentrega do broker.
        var indices = await (await _banco.GetCollection<EventoProcessado>("eventosProcessados")
            .Indexes.ListAsync()).ToListAsync();

        var ttl = Assert.Single(indices, i => i["name"].AsString == "ttl_processadoEm");

        Assert.Equal(IdempotenciaMongo.Retencao.TotalSeconds, ttl["expireAfterSeconds"].ToDouble());
    }
}
