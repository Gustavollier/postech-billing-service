using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing;

/// <summary>
/// Monta o orçamento a partir do diagnóstico.
///
/// Função pura, sem banco e sem broker, pelo mesmo motivo do orquestrador: a
/// regra que calcula dinheiro é a que mais precisa de teste, e ela se testa
/// melhor sozinha.
///
/// O preço usado é o que veio no evento, e não uma consulta à tabela de peças.
/// O orçamento tem que valer o que valia no dia em que foi montado — sem isso,
/// uma mudança na tabela de preço reescreveria orçamentos já enviados ao
/// cliente.
/// </summary>
public static class MontadorDeOrcamento
{
    public static Orcamento Montar(
        DiagnosticoConcluido diagnostico,
        DadosDoCliente cliente,
        string correlationId,
        DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(diagnostico);
        ArgumentNullException.ThrowIfNull(cliente);

        if (diagnostico.Itens.Count == 0)
            throw new ArgumentException("Um diagnóstico sem itens não gera orçamento.", nameof(diagnostico));

        var itens = diagnostico.Itens
            .Select(i => new ItemDoOrcamento
            {
                Descricao = i.Descricao,
                Tipo = i.Tipo,
                Quantidade = i.Quantidade,
                ValorUnitario = i.ValorUnitario,
                Subtotal = i.Quantidade * i.ValorUnitario,
            })
            .ToList();

        var orcamento = new Orcamento
        {
            IdOrdem = diagnostico.IdOrdem,
            IdCliente = cliente.IdCliente,
            NomeCliente = cliente.Nome,
            EmailCliente = cliente.Email,
            Itens = itens,
            Valor = itens.Sum(i => i.Subtotal),
            Status = EStatusOrcamento.Pendente,
            CriadoEm = agora,
        };

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new OrcamentoGerado(orcamento.IdOrdem, orcamento.Id, orcamento.Valor, agora),
            correlationId,
            Origem,
            agora));

        return orcamento;
    }

    /// <summary>
    /// O cliente aprovou. A cobrança no Mercado Pago vem depois, disparada pelo
    /// evento — aprovar e cobrar são dois passos porque a cobrança depende de um
    /// serviço externo que pode estar fora.
    /// </summary>
    public static void Aprovar(Orcamento orcamento, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        if (orcamento.Status != EStatusOrcamento.Pendente)
            throw new InvalidOperationException(
                $"Só um orçamento pendente pode ser aprovado. Este está {orcamento.Status}.");

        orcamento.Status = EStatusOrcamento.Aprovado;
        orcamento.RespondidoEm = agora;

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new OrcamentoAprovado(orcamento.IdOrdem, orcamento.Id, orcamento.Valor, agora),
            correlationId,
            Origem,
            agora));
    }

    public static void Rejeitar(Orcamento orcamento, string motivo, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        if (orcamento.Status != EStatusOrcamento.Pendente)
            throw new InvalidOperationException(
                $"Só um orçamento pendente pode ser rejeitado. Este está {orcamento.Status}.");

        orcamento.Status = EStatusOrcamento.Rejeitado;
        orcamento.MotivoDaRecusa = motivo;
        orcamento.RespondidoEm = agora;

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new OrcamentoRejeitado(orcamento.IdOrdem, orcamento.Id, motivo, agora),
            correlationId,
            Origem,
            agora));
    }

    /// <summary>
    /// Compensação vinda do orquestrador. Diferente de rejeitar, isto não pede
    /// nada ao cliente: a saga desistiu e o orçamento deixa de valer.
    ///
    /// Cancelar duas vezes é no-op de propósito — o evento chega ao menos uma
    /// vez, e a segunda entrega não pode virar erro.
    /// </summary>
    public static void Cancelar(Orcamento orcamento, string motivo, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        if (orcamento.Status == EStatusOrcamento.Cancelado)
            return;

        orcamento.Status = EStatusOrcamento.Cancelado;
        orcamento.MotivoDaRecusa = motivo;
        orcamento.RespondidoEm = agora;
    }

    public const string Origem = "billing-service";
}

/// <summary>
/// O que o Billing sabe do cliente, vindo do evento <c>OrdemAberta</c> e não de
/// uma chamada ao Identidade. Dado duplicado de propósito (ADR-0007): sem isso,
/// gerar orçamento passaria a depender do Identidade estar no ar.
/// </summary>
public sealed record DadosDoCliente(int IdCliente, string Nome, string Email);
