using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MongoDB.Driver;
using PosTech.Contratos;
using PosTech.Mensageria;
using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace PosTech.Billing.Testes;

/// <summary>
/// A API do Billing no ar, com MongoDB e RabbitMQ de verdade.
///
/// É <c>ICollectionFixture</c> e não <c>IAsyncLifetime</c> na classe de teste
/// porque o xUnit instancia a classe uma vez por teste: cada <c>[Fact]</c>
/// subiria o seu par de contêineres. Aqui sobem uma vez e todos os testes da
/// coleção usam o mesmo ambiente, que é também o que evita dois consumidores
/// disputando a mesma fila.
/// </summary>
public sealed class AmbienteDoBilling : IAsyncLifetime
{
    private readonly MongoDbContainer _mongo = InfraDeTeste.Mongo();
    private readonly RabbitMqContainer _rabbit = InfraDeTeste.Rabbit();

    /// <summary>O adquirente, sob controle do teste.</summary>
    public GatewayFalso Gateway { get; } = new();

    public WebApplicationFactory<Program> Fabrica { get; private set; } = null!;

    public IMongoDatabase Banco { get; private set; } = null!;

    public string RabbitConnectionString { get; private set; } = string.Empty;

    /// <summary>
    /// Um cliente HTTP. Com <paramref name="correlationId"/>, manda o
    /// <c>X-Correlation-ID</c> em toda chamada — que é como o APIM chama a API
    /// em produção, e é de onde os controllers tiram a correlação que vai no
    /// evento publicado.
    /// </summary>
    public HttpClient Cliente(string? correlationId = null)
    {
        var cliente = Fabrica.CreateClient();

        if (correlationId is { Length: > 0 })
            cliente.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        return cliente;
    }

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_mongo.StartAsync(), _rabbit.StartAsync());

        RabbitConnectionString = _rabbit.GetConnectionString();
        Banco = new MongoClient(_mongo.GetConnectionString()).GetDatabase("billing-db");

        Fabrica = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("MongoConnectionString", _mongo.GetConnectionString());
            builder.UseSetting("RabbitMqConnectionString", RabbitConnectionString);
            builder.UseSetting("MongoDatabase", "billing-db");

            // Troca o adquirente pelo dublê. É a única coisa substituída: Mongo,
            // RabbitMQ, consumidor e publicador do outbox são os de verdade,
            // porque é justamente o caminho deles que precisa ser provado.
            builder.ConfigureServices(servicos =>
            {
                servicos.RemoveAll<IGatewayDePagamento>();
                servicos.AddSingleton<IGatewayDePagamento>(Gateway);
            });
        });

        // Força a subida do host: é o CreateClient que constrói o grafo de
        // dependências e inicia os serviços de fundo.
        _ = Fabrica.CreateClient();
    }

    public async Task DisposeAsync()
    {
        await Fabrica.DisposeAsync();
        await Task.WhenAll(_mongo.DisposeAsync().AsTask(), _rabbit.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Publica um evento no exchange como outro serviço faria, passando pelo
    /// mesmo envelope e pela mesma routing key da produção.
    /// </summary>
    public async Task PublicarComoAsync<T>(T evento, string origem, string correlationId)
        where T : notnull
    {
        await using var conexao = new ConexaoRabbitMq(RabbitConnectionString, origem);

        await conexao.DeclararTopologiaAsync();

        var publicador = new PublicadorRabbitMq(conexao);

        await publicador.PublicarAsync(Outbox.Enfileirar(evento, correlationId, origem));
    }

    /// <summary>Lê o orçamento direto do Mongo, sem passar pela API.</summary>
    public Task<Orcamento?> OrcamentoDaOrdemAsync(int idOrdem) =>
        new OrcamentoRepositorio(Banco).ObterPorOrdemAsync(idOrdem);
}

[CollectionDefinition("billing")]
public sealed class ColecaoDoBilling : ICollectionFixture<AmbienteDoBilling>;

/// <summary>
/// Adquirente de mentira, com o resultado sob controle do teste.
///
/// Guarda o que foi cobrado para os testes conseguirem afirmar que a cobrança
/// aconteceu uma vez só — que é o que a chave de idempotência garante contra o
/// Mercado Pago de verdade.
/// </summary>
public sealed class GatewayFalso : IGatewayDePagamento
{
    private int _sequencia;

    public Cobranca Resposta { get; set; } =
        new(EResultadoDaCobranca.Pendente, "mp-0", null, "https://pagar.test/0");

    /// <summary>Quando preenchido, a criação da cobrança falha com isto.</summary>
    public Exception? Falha { get; set; }

    public List<string> Cobradas { get; } = [];

    public Task<Cobranca> CriarCobrancaAsync(
        Orcamento orcamento, string correlationId, CancellationToken cancellationToken = default)
    {
        if (Falha is not null)
            throw Falha;

        Cobradas.Add(orcamento.Id);

        var referencia = Resposta.ReferenciaExterna is { Length: > 0 } fixa
            ? fixa
            : $"mp-{Interlocked.Increment(ref _sequencia)}";

        return Task.FromResult(Resposta with { ReferenciaExterna = referencia });
    }

    public Task<Cobranca> ConsultarAsync(string referenciaExterna, CancellationToken cancellationToken = default) =>
        Task.FromResult(Resposta with { ReferenciaExterna = referenciaExterna });

    public void Reiniciar()
    {
        Falha = null;
        Cobradas.Clear();
        Resposta = new Cobranca(EResultadoDaCobranca.Pendente, string.Empty, null, "https://pagar.test");
    }
}

/// <summary>
/// Escuta a fila do OS Service, que é para onde vai tudo o que o Billing
/// publica. É o observador que prova que o evento saiu de verdade pelo broker,
/// e não só foi gravado no documento.
/// </summary>
public sealed class EspiaoDaFila : IAsyncDisposable
{
    private readonly ConexaoRabbitMq _conexao;
    private readonly ConsumidorDeEventos _consumidor;
    private readonly List<EnvelopeRecebido> _recebidos = [];
    private readonly object _trava = new();

    private EspiaoDaFila(ConexaoRabbitMq conexao, ConsumidorDeEventos consumidor)
    {
        _conexao = conexao;
        _consumidor = consumidor;
    }

    public static async Task<EspiaoDaFila> LigarAsync(string connectionString, string fila, params string[] tipos)
    {
        var conexao = new ConexaoRabbitMq(connectionString, "espiao");

        await conexao.DeclararTopologiaAsync();

        EspiaoDaFila? espiao = null;

        var tratadores = tipos.ToDictionary(
            tipo => tipo,
            _ => (TratadorDeEvento)((envelope, _) =>
            {
                espiao!.Guardar(envelope);
                return Task.CompletedTask;
            }));

        var consumidor = new ConsumidorDeEventos(
            conexao, fila, "espiao", new IdempotenciaEmMemoria(), tratadores);

        espiao = new EspiaoDaFila(conexao, consumidor);

        await consumidor.IniciarAsync();

        return espiao;
    }

    private void Guardar(EnvelopeRecebido envelope)
    {
        lock (_trava)
        {
            _recebidos.Add(envelope);
        }
    }

    public IReadOnlyList<EnvelopeRecebido> Recebidos
    {
        get
        {
            lock (_trava)
            {
                return [.. _recebidos];
            }
        }
    }

    /// <summary>
    /// Espera um evento chegar, filtrando pelo <c>correlationId</c>.
    ///
    /// O filtro não é luxo: a fila do orquestrador não é drenada por ninguém
    /// nestes testes, então ela acumula o que os testes anteriores produziram.
    /// Procurar só pelo tipo acha o evento de outra ordem e a asserção passa ou
    /// falha por acidente — foi exatamente o que aconteceu na primeira rodada.
    /// </summary>
    public Task<EnvelopeRecebido> EsperarAsync(string tipo, string correlationId, int segundos = 45) =>
        InfraDeTeste.AguardarAsync(
            () => Task.FromResult(
                Recebidos.FirstOrDefault(e => e.Tipo == tipo && e.CorrelationId == correlationId)),
            $"o evento {tipo} de {correlationId} chegar na fila",
            segundos);

    public async ValueTask DisposeAsync()
    {
        await _consumidor.DisposeAsync();
        await _conexao.DisposeAsync();
    }
}
