using System.Text;
using PosTech.Contratos;
using RabbitMQ.Client;

namespace PosTech.Mensageria;

/// <summary>
/// Entrega um evento ao broker.
///
/// A interface não conhece RabbitMQ de propósito: o processador do outbox é
/// testável contra um dublê, e trocar de broker não mexe na regra do outbox.
/// </summary>
public interface IPublicadorDeEventos
{
    Task PublicarAsync(EventoOutbox evento, CancellationToken cancellationToken = default);
}

/// <summary>
/// Publicador com confirmação do broker ligada.
///
/// A confirmação é o que torna o outbox honesto. Sem ela, <c>BasicPublish</c>
/// devolve assim que o byte sai do processo, e o outbox marcaria como publicado
/// um evento que o broker nunca aceitou. Com <c>publisherConfirmations</c>, o
/// await só volta quando o RabbitMQ confirmou, e um erro vira exceção que
/// mantém a linha pendente para a próxima tentativa.
/// </summary>
public sealed class PublicadorRabbitMq : IPublicadorDeEventos, IAsyncDisposable
{
    private readonly ConexaoRabbitMq _conexao;

    public PublicadorRabbitMq(ConexaoRabbitMq conexao)
    {
        _conexao = conexao ?? throw new ArgumentNullException(nameof(conexao));
    }

    public async Task PublicarAsync(EventoOutbox evento, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evento);

        var canal = await _conexao.ObterCanalDePublicacaoAsync(cancellationToken).ConfigureAwait(false);

        var propriedades = new BasicProperties
        {
            // Persistente: mensagem sobrevive a um restart do broker. O plano
            // gratuito do CloudAMQP reinicia sem avisar, e um evento de saga
            // perdido ali deixaria uma ordem travada no meio do fluxo.
            Persistent = true,
            MessageId = evento.MensagemId.ToString(),
            CorrelationId = evento.CorrelationId,
            Type = evento.Tipo,
            ContentType = "application/json",
            Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        };

        var corpo = Encoding.UTF8.GetBytes(evento.Payload);

        await canal.BasicPublishAsync(
            exchange: Topologia.Exchange,
            routingKey: evento.RoutingKey,
            mandatory: false,
            basicProperties: propriedades,
            body: corpo,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _conexao.DisposeAsync();
}

/// <summary>
/// Conexão e canal únicos, criados sob demanda.
///
/// Conexão AMQP é cara e feita para durar; canal é barato mas não é seguro para
/// uso concorrente. Um canal por publicador, protegido por semáforo, é o
/// arranjo mais simples que não corrompe o protocolo sob carga.
/// </summary>
public sealed class ConexaoRabbitMq : IAsyncDisposable
{
    private readonly ConnectionFactory _fabrica;
    private readonly SemaphoreSlim _trava = new(1, 1);

    private IConnection? _conexao;
    private IChannel? _canal;

    public ConexaoRabbitMq(string connectionString, string nomeDoServico)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("Connection string do RabbitMQ é obrigatória.", nameof(connectionString));

        _fabrica = new ConnectionFactory
        {
            Uri = new Uri(connectionString),
            // O nome aparece no painel do CloudAMQP. Com quatro serviços na mesma
            // instância gratuita, saber de quem é a conexão aberta vale muito na
            // hora de entender uma fila parada.
            ClientProvidedName = nomeDoServico,
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
        };
    }

    /// <summary>
    /// O canal de publicação, um por processo, com confirmação ligada.
    ///
    /// Publicar e consumir NÃO podem dividir canal. Canal não é seguro para uso
    /// concorrente, e o <c>BasicQos</c> que o consumidor define vale para o
    /// canal inteiro — num canal compartilhado ele passaria a limitar também o
    /// publicador. Pior: a confirmação de publicação e a entrega ao consumidor
    /// se intercalariam no mesmo fluxo do protocolo.
    /// </summary>
    public async Task<IChannel> ObterCanalDePublicacaoAsync(CancellationToken cancellationToken = default)
    {
        if (_canal is { IsOpen: true })
            return _canal;

        await _trava.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_canal is { IsOpen: true })
                return _canal;

            var conexao = await ObterConexaoAsync(cancellationToken).ConfigureAwait(false);

            var opcoes = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);

            _canal = await conexao.CreateChannelAsync(opcoes, cancellationToken).ConfigureAwait(false);

            return _canal;
        }
        finally
        {
            _trava.Release();
        }
    }

    /// <summary>
    /// Um canal novo, para quem precisa do seu. Consumidor usa este: cada
    /// consumidor tem o seu canal, com o seu próprio prefetch.
    ///
    /// Quem chama é dono do canal e responde por fechá-lo.
    /// </summary>
    public async Task<IChannel> CriarCanalAsync(CancellationToken cancellationToken = default)
    {
        var conexao = await ObterConexaoAsync(cancellationToken).ConfigureAwait(false);

        return await conexao.CreateChannelAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<IConnection> ObterConexaoAsync(CancellationToken cancellationToken)
    {
        if (_conexao is { IsOpen: true })
            return _conexao;

        _conexao ??= await _fabrica.CreateConnectionAsync(cancellationToken).ConfigureAwait(false);

        return _conexao;
    }

    /// <summary>
    /// Declara exchange, filas, dead letters e bindings a partir de
    /// <see cref="Topologia"/>. Idempotente: rodar na subida de todo serviço é
    /// seguro e evita a ordem de inicialização virar requisito.
    /// </summary>
    public async Task DeclararTopologiaAsync(CancellationToken cancellationToken = default)
    {
        // Canal descartável: declarar topologia é operação de partida e um erro
        // aqui (fila declarada antes com outros argumentos) fecha o canal. Num
        // canal compartilhado isso derrubaria publicação e consumo junto.
        await using var canal = await CriarCanalAsync(cancellationToken).ConfigureAwait(false);

        await canal.ExchangeDeclareAsync(
            Topologia.Exchange, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await canal.ExchangeDeclareAsync(
            Topologia.ExchangeDeFalhas, ExchangeType.Topic, durable: true, autoDelete: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        foreach (var (fila, chaves) in Topologia.Assinaturas)
        {
            var dlq = Topologia.DeadLetterDe(fila);

            await canal.QueueDeclareAsync(
                dlq, durable: true, exclusive: false, autoDelete: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            await canal.QueueBindAsync(
                dlq, Topologia.ExchangeDeFalhas, routingKey: "#",
                cancellationToken: cancellationToken).ConfigureAwait(false);

            var argumentos = new Dictionary<string, object?>
            {
                ["x-dead-letter-exchange"] = Topologia.ExchangeDeFalhas,
            };

            await canal.QueueDeclareAsync(
                fila, durable: true, exclusive: false, autoDelete: false,
                arguments: argumentos,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (var chave in chaves)
            {
                await canal.QueueBindAsync(
                    fila, Topologia.Exchange, chave,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_canal is not null)
            await _canal.DisposeAsync().ConfigureAwait(false);

        if (_conexao is not null)
            await _conexao.DisposeAsync().ConfigureAwait(false);

        _trava.Dispose();
    }
}
