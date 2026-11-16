using PosTech.Seguranca;

namespace PosTech.Billing.Testes;


/// <summary>
/// A leitura do token. É a base de toda decisão de acesso do serviço, então o
/// que importa aqui é o que ela faz com entrada torta: qualquer coisa que não
/// seja um token legível e dentro da validade tem que virar anônimo, nunca
/// exceção e nunca um perfil.
/// </summary>
public class QuemChamaTests
{
    private static QuemChama Ler(string? cabecalho) => QuemChama.Ler(cabecalho, DateTimeOffset.UtcNow);

    [Fact]
    public void Token_de_funcionario_e_equipe_e_gerente_so_quando_o_cargo_e_gerente()
    {
        var gerente = Ler($"Bearer {TokenDeTeste.Gerente(7)}");
        var mecanico = Ler($"Bearer {TokenDeTeste.Mecanico(8)}");

        Assert.True(gerente.EhEquipe);
        Assert.True(gerente.EhGerente);
        Assert.Equal(7, gerente.IdFuncionario);

        Assert.True(mecanico.EhEquipe);
        Assert.False(mecanico.EhGerente);
        Assert.False(mecanico.EhCliente);
    }

    [Fact]
    public void Token_de_cliente_ve_o_que_e_dele_e_mais_nada()
    {
        var cliente = Ler($"Bearer {TokenDeTeste.Cliente(10)}");

        Assert.True(cliente.EhCliente);
        Assert.False(cliente.EhEquipe);
        Assert.True(cliente.PodeVerDadosDoCliente(10));
        Assert.False(cliente.PodeVerDadosDoCliente(11));
    }

    [Fact]
    public void A_equipe_ve_os_dados_de_qualquer_cliente()
    {
        Assert.True(Ler($"Bearer {TokenDeTeste.Mecanico()}").PodeVerDadosDoCliente(999));
    }

    [Fact]
    public void Cliente_sem_id_no_token_nao_ve_cliente_nenhum()
    {
        var semId = Ler($"Bearer {TokenDeTeste.Montar("Cliente")}");

        Assert.True(semId.EhCliente);
        Assert.False(semId.PodeVerDadosDoCliente(0));
        Assert.False(semId.PodeVerDadosDoCliente(1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXN1YXJpbzpzZW5oYQ==")]
    [InlineData("Bearer nao-e-um-jwt")]
    [InlineData("Bearer a.b.c")]
    [InlineData("Bearer e30.e30.x")]
    [InlineData("Bearer e30.bnVsbA.x")]
    public void O_que_nao_e_um_token_legivel_com_perfil_vira_anonimo(string? cabecalho)
    {
        var quem = Ler(cabecalho);

        Assert.False(quem.Autenticado);
        Assert.False(quem.EhEquipe);
        Assert.False(quem.PodeVerDadosDoCliente(1));
    }

    [Fact]
    public void Token_vencido_vira_anonimo()
    {
        Assert.False(Ler($"Bearer {TokenDeTeste.Vencido()}").Autenticado);
    }
}
