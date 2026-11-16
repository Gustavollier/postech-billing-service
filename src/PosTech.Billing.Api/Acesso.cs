using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace PosTech.Seguranca;

/// <summary>
/// Quem está chamando, lido do JWT que o gateway repassa.
///
/// O APIM valida o token — assinatura, emissor, audiência e validade — e é o
/// único cliente dos serviços. O que ele não faz é decidir o que cada perfil
/// pode: para ele, token de cliente e de gerente são igualmente válidos. Sem
/// esta leitura aqui, um cliente autenticado enxergava as ordens, os
/// orçamentos e o cadastro da oficina inteira.
///
/// A assinatura não é conferida de novo de propósito: a chave fica no gateway
/// e no Identidade, e espalhá-la por quatro serviços aumentaria a superfície
/// sem acrescentar garantia, já que o tráfego só chega pelo gateway. O que este
/// código decide é autorização, com as claims de um token já autenticado.
/// </summary>
public sealed record QuemChama(string? Perfil, int? IdCliente, int? IdFuncionario)
{
    public const string PerfilCliente = "Cliente";
    public const string PerfilGerente = "Gerente";

    public static readonly QuemChama Anonimo = new(null, null, null);

    public bool Autenticado => !string.IsNullOrEmpty(Perfil);

    public bool EhCliente => Perfil == PerfilCliente;

    /// <summary>Qualquer cargo da oficina. Cliente é o único perfil de fora.</summary>
    public bool EhEquipe => Autenticado && !EhCliente;

    public bool EhGerente => Perfil == PerfilGerente;

    /// <summary>
    /// A equipe vê qualquer cliente; o cliente, só o próprio cadastro e o que
    /// pendura nele — ordem, orçamento, veículo.
    /// </summary>
    public bool PodeVerDadosDoCliente(int idCliente) =>
        EhEquipe || (EhCliente && IdCliente == idCliente);

    public static QuemChama De(HttpContext contexto)
    {
        if (contexto.Items.TryGetValue(typeof(QuemChama), out var guardado) && guardado is QuemChama pronto)
            return pronto;

        var quem = Ler(contexto.Request.Headers.Authorization.ToString(), DateTimeOffset.UtcNow);
        contexto.Items[typeof(QuemChama)] = quem;
        return quem;
    }

    public static QuemChama Ler(string? cabecalho, DateTimeOffset agora)
    {
        const string prefixo = "Bearer ";

        if (string.IsNullOrWhiteSpace(cabecalho) || !cabecalho.StartsWith(prefixo, StringComparison.OrdinalIgnoreCase))
            return Anonimo;

        var partes = cabecalho[prefixo.Length..].Trim().Split('.');
        if (partes.Length != 3)
            return Anonimo;

        try
        {
            using var claims = JsonDocument.Parse(DecodificarBase64Url(partes[1]));
            var raiz = claims.RootElement;

            // Um payload que é JSON válido mas não é objeto ("null", um número)
            // não tem claim nenhuma para ler.
            if (raiz.ValueKind != JsonValueKind.Object)
                return Anonimo;

            // Vencido é anônimo. O gateway já barra, mas a regra não depende dele.
            if (raiz.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var segundos)
                && DateTimeOffset.FromUnixTimeSeconds(segundos) <= agora)
                return Anonimo;

            var perfil = Texto(raiz, "role")
                ?? Texto(raiz, "http://schemas.microsoft.com/ws/2008/06/identity/claims/role");

            return string.IsNullOrWhiteSpace(perfil)
                ? Anonimo
                : new QuemChama(perfil, Inteiro(raiz, "ClienteId"), Inteiro(raiz, "FuncionarioId"));
        }
        catch (Exception excecao) when (excecao is JsonException or FormatException or ArgumentException)
        {
            return Anonimo;
        }
    }

    private static byte[] DecodificarBase64Url(string valor)
    {
        var base64 = valor.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        return Convert.FromBase64String(base64);
    }

    private static string? Texto(JsonElement raiz, string nome)
    {
        if (!raiz.TryGetProperty(nome, out var valor))
            return null;

        return valor.ValueKind switch
        {
            JsonValueKind.String => valor.GetString(),
            JsonValueKind.Number => valor.GetRawText(),
            // Mais de um papel no mesmo token: vale o primeiro.
            JsonValueKind.Array when valor.GetArrayLength() > 0 => valor[0].GetString(),
            _ => null,
        };
    }

    private static int? Inteiro(JsonElement raiz, string nome) =>
        int.TryParse(Texto(raiz, nome), out var numero) ? numero : null;
}

public enum Perfil
{
    /// <summary>Qualquer token válido: cliente ou equipe. A posse é conferida na ação.</summary>
    Autenticado,

    /// <summary>Qualquer cargo da oficina.</summary>
    Equipe,

    /// <summary>Só o gerente: escrita administrativa.</summary>
    Gerente,
}

/// <summary>
/// Exige um perfil para a ação ou o controller. Sem token, 401; com token de
/// perfil insuficiente, 403. A anotação na ação vence a do controller.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ExigeAttribute : Attribute, IAuthorizationFilter, IOrderedFilter
{
    private readonly Perfil _perfil;

    public ExigeAttribute(Perfil perfil) => _perfil = perfil;

    // Filtros de ação rodam depois dos de controller. Quando a ação tem a
    // própria exigência, é ela que decide — é o que permite um controller
    // "só equipe" abrir uma leitura para o cliente dono.
    public int Order => 0;

    public void OnAuthorization(AuthorizationFilterContext contexto)
    {
        var maisEspecifica = contexto.Filters.OfType<ExigeAttribute>().LastOrDefault();
        if (!ReferenceEquals(maisEspecifica, this))
            return;

        var quem = QuemChama.De(contexto.HttpContext);

        if (!quem.Autenticado)
        {
            contexto.Result = new ObjectResult(new { erro = "Token ausente, inválido ou vencido." })
            {
                StatusCode = StatusCodes.Status401Unauthorized,
            };
            return;
        }

        var permitido = _perfil switch
        {
            Perfil.Autenticado => true,
            Perfil.Equipe => quem.EhEquipe,
            Perfil.Gerente => quem.EhGerente,
            _ => false,
        };

        if (!permitido)
        {
            contexto.Result = new ObjectResult(new { erro = "Seu perfil não tem acesso a esta operação." })
            {
                StatusCode = StatusCodes.Status403Forbidden,
            };
        }
    }
}
