using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

/// <summary>
/// Tokens para os testes de API, no formato dos que o Identidade e a Auth
/// Function emitem: <c>role</c>, <c>ClienteId</c> ou <c>FuncionarioId</c>, e
/// <c>exp</c>.
///
/// A assinatura é fictícia de propósito. Os serviços não a conferem — quem
/// confere é o gateway, que em teste não existe —; o que os testes exercitam é
/// a decisão de autorização a partir das claims.
/// </summary>
public static class TokenDeTeste
{
    public static string Gerente(int idFuncionario = 1) => Montar("Gerente", idFuncionario: idFuncionario);

    public static string Mecanico(int idFuncionario = 2) => Montar("Mecanico", idFuncionario: idFuncionario);

    public static string Cliente(int idCliente) => Montar("Cliente", idCliente: idCliente);

    public static string Vencido() =>
        Montar("Gerente", idFuncionario: 1, expiraEm: DateTimeOffset.UtcNow.AddMinutes(-1));

    public static string Montar(
        string perfil, int? idCliente = null, int? idFuncionario = null, DateTimeOffset? expiraEm = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["role"] = perfil,
            ["exp"] = (expiraEm ?? DateTimeOffset.UtcNow.AddMinutes(15)).ToUnixTimeSeconds(),
            ["iss"] = "PosTechChallenge",
            ["aud"] = "PosTechChallenge-API",
        };

        if (idCliente is not null)
            claims["ClienteId"] = idCliente.Value.ToString();

        if (idFuncionario is not null)
            claims["FuncionarioId"] = idFuncionario.Value.ToString();

        return $"{Base64Url("""{"alg":"HS256","typ":"JWT"}""")}.{Base64Url(JsonSerializer.Serialize(claims))}.assinatura";
    }

    /// <summary>Troca quem chama: um token, ou <c>null</c> para chamar sem nenhum.</summary>
    public static HttpClient Como(this HttpClient http, string? token)
    {
        http.DefaultRequestHeaders.Authorization =
            token is null ? null : new AuthenticationHeaderValue("Bearer", token);

        return http;
    }

    private static string Base64Url(string texto) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(texto)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
