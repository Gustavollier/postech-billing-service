using Testcontainers.MongoDb;
using Testcontainers.RabbitMq;

namespace PosTech.Billing.Testes;

/// <summary>
/// Os contêineres de infraestrutura dos testes, num lugar só.
///
/// Existe por causa de um problema de ambiente que custou um CI vermelho: o
/// runner do GitHub roda kernel 6.19, e o MongoDB 8 **se recusa a subir** nele
/// — aborta na partida com uma mensagem apontando o SERVER-121912. Na máquina
/// de quem desenvolve (kernel 6.18) sobe sem reclamar, então o teste passa
/// local e falha no CI.
///
/// A correção oficial é do lado do kernel (6.18 ou anterior, ou 7.0.14 em
/// diante), o que não se escolhe num runner hospedado. O que resta é o
/// contorno: <c>glibc.pthread.rseq=1</c>, que faz o mongod subir no 6.19.
///
/// Fica centralizado aqui para a hora em que isso deixar de ser necessário ser
/// uma linha, e não uma caça pelos arquivos de teste.
/// </summary>
public static class InfraDeTeste
{
    /// <summary>
    /// A imagem do Mongo. Sobrescrevível por variável de ambiente para dar um
    /// caminho de fuga sem recompilar, caso a mesma incompatibilidade volte
    /// numa versão nova.
    /// </summary>
    public static string ImagemDoMongo =>
        Environment.GetEnvironmentVariable("POSTECH_MONGO_IMAGE") is { Length: > 0 } imagem
            ? imagem
            : "mongo:8";

    public static MongoDbContainer Mongo() =>
        new MongoDbBuilder(ImagemDoMongo)
            .WithEnvironment("GLIBC_TUNABLES", "glibc.pthread.rseq=1")
            .Build();

    public static RabbitMqContainer Rabbit() =>
        new RabbitMqBuilder("rabbitmq:4").Build();

    /// <summary>
    /// Espera uma condição assíncrona virar verdadeira.
    ///
    /// Fluxo por fila não tem como ser verificado logo depois de publicar: o
    /// consumidor está noutra thread, e o publicador do outbox só acorda de dois
    /// em dois segundos. Sem isto, cada teste assíncrono teria o seu próprio
    /// <c>Task.Delay</c> chutado — que é lento quando acerta e instável quando
    /// erra.
    /// </summary>
    public static async Task<T> AguardarAsync<T>(
        Func<Task<T?>> tentativa,
        string oQueEsperava,
        int segundos = 45)
        where T : class
    {
        var limite = DateTime.UtcNow.AddSeconds(segundos);

        while (DateTime.UtcNow < limite)
        {
            if (await tentativa().ConfigureAwait(false) is { } resultado)
                return resultado;

            await Task.Delay(250).ConfigureAwait(false);
        }

        throw new TimeoutException($"Esperei {segundos}s por: {oQueEsperava}. Não aconteceu.");
    }
}
