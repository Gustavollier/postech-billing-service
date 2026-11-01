using MongoDB.Driver;
using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing.Api.Aplicacao;

/// <summary>
/// Entrega o que está no outbox, em laço. Mesma razão do OS Service: a
/// requisição HTTP termina quando o documento grava, sem esperar o broker.
/// </summary>
public sealed class PublicadorDoOutbox : BackgroundService
{
    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan IntervaloAposFalha = TimeSpan.FromSeconds(15);

    private readonly ProcessadorDoOutbox _processador;
    private readonly ILogger<PublicadorDoOutbox> _log;

    public PublicadorDoOutbox(ProcessadorDoOutbox processador, ILogger<PublicadorDoOutbox> log)
    {
        _processador = processador;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = Intervalo;

            try
            {
                var publicados = await _processador.ProcessarLoteAsync(cancellationToken: stoppingToken);

                if (publicados > 0)
                    _log.LogInformation("{Quantidade} eventos publicados.", publicados);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception excecao)
            {
                _log.LogError(excecao, "Falha no laço do outbox.");
                espera = IntervaloAposFalha;
            }

            try
            {
                await Task.Delay(espera, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}

/// <summary>Consome a fila do Billing e passa para o manipulador.</summary>
public sealed class ConsumidorDoBilling : BackgroundService
{
    private readonly ConexaoRabbitMq _conexao;
    private readonly IServiceScopeFactory _escopos;
    private readonly IRegistroDeIdempotencia _idempotencia;
    private readonly ILogger<ConsumidorDoBilling> _log;

    private ConsumidorDeEventos? _consumidor;

    public ConsumidorDoBilling(
        ConexaoRabbitMq conexao,
        IServiceScopeFactory escopos,
        IRegistroDeIdempotencia idempotencia,
        ILogger<ConsumidorDoBilling> log)
    {
        _conexao = conexao;
        _escopos = escopos;
        _idempotencia = idempotencia;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await _conexao.DeclararTopologiaAsync(stoppingToken);

        // Os nomes vêm do manipulador, para a lista não divergir dele: um
        // evento tratado no código e esquecido aqui nunca chegaria.
        using var escopoInicial = _escopos.CreateScope();
        var nomes = escopoInicial.ServiceProvider
            .GetRequiredService<ManipuladorDeEventos>().Tratadores().Keys.ToList();

        var tratadores = nomes.ToDictionary(nome => nome, nome => (TratadorDeEvento)((envelope, ct) =>
            DespacharAsync(nome, envelope, ct)));

        _consumidor = new ConsumidorDeEventos(
            _conexao, Topologia.FilaBilling, PagamentoDoOrcamento.Origem, _idempotencia, tratadores);

        await _consumidor.IniciarAsync(stoppingToken);

        _log.LogInformation("Consumindo {Fila}.", Topologia.FilaBilling);

        await Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task DespacharAsync(string tipo, EnvelopeRecebido envelope, CancellationToken cancellationToken)
    {
        using var escopo = _escopos.CreateScope();
        var manipulador = escopo.ServiceProvider.GetRequiredService<ManipuladorDeEventos>();

        using var _ = _log.BeginScope(new Dictionary<string, object>
        {
            ["correlationId"] = envelope.CorrelationId,
        });

        await manipulador.Tratadores()[tipo](envelope, cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_consumidor is not null)
            await _consumidor.DisposeAsync();

        await base.StopAsync(cancellationToken);
    }
}

/// <summary>
/// Cria os índices do Mongo na partida.
///
/// Idempotente, como o schema SQL dos outros serviços. O índice único por ordem
/// é o que impede duas entregas do mesmo <c>DiagnosticoConcluido</c> virarem
/// dois orçamentos.
/// </summary>
public sealed class CriadorDeIndices : IHostedService
{
    private readonly IMongoDatabase _banco;
    private readonly ILogger<CriadorDeIndices> _log;

    public CriadorDeIndices(IMongoDatabase banco, ILogger<CriadorDeIndices> log)
    {
        _banco = banco;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await new OrcamentoRepositorio(_banco).GarantirIndicesAsync(cancellationToken);
            await new IdempotenciaMongo(_banco).GarantirIndicesAsync(cancellationToken);
            _log.LogInformation("Índices do billing-db garantidos.");
        }
        catch (Exception excecao)
        {
            // Não derruba o serviço: o Mongo pode ainda estar subindo, e o
            // índice é garantia e não pré-requisito para o código funcionar.
            _log.LogError(excecao, "Não foi possível garantir os índices do billing-db.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
