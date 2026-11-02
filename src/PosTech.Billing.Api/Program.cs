using System.Text.Json.Serialization;
using MongoDB.Driver;
using PosTech.Billing;
using PosTech.Billing.Api.Aplicacao;
using PosTech.Billing.Api.Infra;
using PosTech.Contratos;
using PosTech.Mensageria;

var builder = WebApplication.CreateBuilder(args);

var mongoConnectionString = builder.Configuration["MongoConnectionString"]
    ?? throw new InvalidOperationException("MongoConnectionString não configurada.");

var rabbitConnectionString = builder.Configuration["RabbitMqConnectionString"]
    ?? throw new InvalidOperationException("RabbitMqConnectionString não configurada.");

var nomeDoBanco = builder.Configuration["MongoDatabase"] ?? "billing-db";

builder.Services.AddControllers()
    .AddJsonOptions(opcoes => opcoes.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHealthChecks();

builder.Logging.AddJsonConsole(opcoes =>
{
    opcoes.IncludeScopes = true;
    opcoes.UseUtcTimestamp = true;
});

builder.Services.AddSingleton<IMongoDatabase>(_ =>
    new MongoClient(mongoConnectionString).GetDatabase(nomeDoBanco));

builder.Services.AddScoped<IOrcamentoRepositorio>(sp =>
    new OrcamentoRepositorio(sp.GetRequiredService<IMongoDatabase>()));
builder.Services.AddScoped<ManipuladorDeEventos>();

builder.Services.AddSingleton<IRepositorioDeOutbox>(sp =>
    new OutboxMongo(sp.GetRequiredService<IMongoDatabase>()));

// Idempotência do consumidor: sem isto, a segunda entrega de um
// DiagnosticoConcluido — que o RabbitMQ faz por desenho — vira um segundo
// orçamento.
builder.Services.AddSingleton<IRegistroDeIdempotencia>(sp =>
    new IdempotenciaMongo(sp.GetRequiredService<IMongoDatabase>()));

// O adquirente. Sem o token configurado o serviço sobe assim mesmo, com um
// gateway que recusa toda cobrança e diz o porquê — melhor que estourar na
// partida e deixar o resto do Billing fora do ar por causa de uma credencial
// que ainda não chegou.
// "MercadoPago:AccessToken" e não "MercadoPago__AccessToken": o .NET troca o
// "__" da variável de ambiente por ":" ao montar a configuração. Lida com
// "__", a chave nunca existe — foi o que fazia a BaseUrl abaixo ser ignorada
// em silêncio, sempre caindo no Mercado Pago real.
var tokenMercadoPago = builder.Configuration["MercadoPago:AccessToken"];

if (string.IsNullOrWhiteSpace(tokenMercadoPago))
{
    builder.Services.AddSingleton<IGatewayDePagamento, GatewayIndisponivel>();
}
else
{
    builder.Services.AddHttpClient(GatewayMercadoPago.NomeDoHttpClient, http =>
    {
        http.BaseAddress = new Uri(builder.Configuration["MercadoPago:BaseUrl"] ?? "https://api.mercadopago.com");
        http.Timeout = TimeSpan.FromSeconds(20);
    });

    builder.Services.AddSingleton<IGatewayDePagamento>(sp => new GatewayMercadoPago(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(GatewayMercadoPago.NomeDoHttpClient),
        tokenMercadoPago));
}

builder.Services.AddSingleton(_ => new ConexaoRabbitMq(rabbitConnectionString, PagamentoDoOrcamento.Origem));
builder.Services.AddSingleton<IPublicadorDeEventos>(sp =>
    new PublicadorRabbitMq(sp.GetRequiredService<ConexaoRabbitMq>()));
builder.Services.AddSingleton(sp => new ProcessadorDoOutbox(
    sp.GetRequiredService<IRepositorioDeOutbox>(),
    sp.GetRequiredService<IPublicadorDeEventos>()));

builder.Services.AddHostedService<PublicadorDoOutbox>();
builder.Services.AddHostedService<ConsumidorDoBilling>();
builder.Services.AddHostedService<CriadorDeIndices>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapControllers();
app.MapHealthChecks("/health");
app.MapGet("/health/ready", () => Results.Ok(new { status = "pronto" }));

app.Run();

/// <summary>Exposto para os testes de integração subirem a API em memória.</summary>
public partial class Program;
