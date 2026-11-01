using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing;

/// <summary>
/// O que acontece com o dinheiro de um orçamento.
///
/// Função pura, como o montador: recebe o orçamento e o resultado da cobrança,
/// muda o documento e enfileira o evento. Quem fala com o Mercado Pago é a
/// camada de fora — aqui só mora a regra.
/// </summary>
public static class PagamentoDoOrcamento
{
    public const string Origem = "billing-service";

    /// <summary>
    /// A cobrança foi criada. Guarda a referência do adquirente e nada mais:
    /// criar cobrança não é pagamento, e anunciar `PagamentoConfirmado` aqui
    /// liberaria a oficina para trabalhar antes de o dinheiro entrar.
    /// </summary>
    public static void RegistrarCobranca(Orcamento orcamento, Cobranca cobranca, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);
        ArgumentNullException.ThrowIfNull(cobranca);

        if (orcamento.Status != EStatusOrcamento.Aprovado)
            throw new InvalidOperationException(
                $"Só um orçamento aprovado pode ser cobrado. Este está {orcamento.Status}.");

        orcamento.ReferenciaExterna = cobranca.ReferenciaExterna;
        orcamento.UrlDePagamento = cobranca.UrlDePagamento;
        orcamento.CobrancaCriadaEm = agora;
    }

    /// <summary>
    /// O dinheiro entrou.
    ///
    /// Idempotente: a notificação do adquirente chega repetida, e confirmar
    /// duas vezes publicaria dois <c>PagamentoConfirmado</c>. O orquestrador
    /// ignoraria o segundo, mas depender disso seria terceirizar a correção.
    /// Devolve <c>false</c> quando não havia nada a fazer.
    /// </summary>
    public static bool Confirmar(Orcamento orcamento, Cobranca cobranca, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);
        ArgumentNullException.ThrowIfNull(cobranca);

        if (orcamento.StatusDoPagamento == EStatusDoPagamento.Confirmado)
            return false;

        if (orcamento.Status == EStatusOrcamento.Cancelado)
            throw new InvalidOperationException(
                "Chegou confirmação de pagamento para um orçamento cancelado. " +
                "Isto precisa de estorno, não de confirmação.");

        orcamento.StatusDoPagamento = EStatusDoPagamento.Confirmado;
        orcamento.ReferenciaExterna = cobranca.ReferenciaExterna;
        orcamento.PagoEm = agora;

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new PagamentoConfirmado(
                orcamento.IdOrdem, IdDoPagamento(orcamento), orcamento.Valor,
                cobranca.ReferenciaExterna, agora),
            correlationId, Origem, agora));

        return true;
    }

    /// <summary>
    /// O adquirente recusou.
    ///
    /// Não cancela o orçamento: o cliente pode tentar outro meio, e a saga
    /// trata isso voltando um passo em vez de desistir.
    /// </summary>
    public static bool Recusar(Orcamento orcamento, string motivo, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        if (orcamento.StatusDoPagamento is EStatusDoPagamento.Confirmado or EStatusDoPagamento.Recusado)
            return false;

        orcamento.StatusDoPagamento = EStatusDoPagamento.Recusado;
        orcamento.MotivoDaRecusaDoPagamento = motivo;

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new PagamentoRecusado(orcamento.IdOrdem, IdDoPagamento(orcamento), motivo, agora),
            correlationId, Origem, agora));

        return true;
    }

    /// <summary>
    /// Compensação: a ordem foi cancelada e o dinheiro precisa voltar.
    ///
    /// Só estorna o que de fato foi pago — a mesma regra do orquestrador.
    /// Devolve <c>false</c> quando não havia pagamento, e aí não há evento
    /// nenhum: anunciar um estorno que não aconteceu inventa movimento
    /// financeiro no extrato de quem lê.
    /// </summary>
    public static bool Estornar(Orcamento orcamento, string motivo, string correlationId, DateTime agora)
    {
        ArgumentNullException.ThrowIfNull(orcamento);

        if (orcamento.StatusDoPagamento != EStatusDoPagamento.Confirmado)
            return false;

        orcamento.StatusDoPagamento = EStatusDoPagamento.Estornado;
        orcamento.EstornadoEm = agora;

        orcamento.EventosPendentes.Add(Outbox.Enfileirar(
            new PagamentoEstornado(
                orcamento.IdOrdem, IdDoPagamento(orcamento), orcamento.ReferenciaExterna ?? string.Empty,
                orcamento.Valor, motivo, agora),
            correlationId, Origem, agora));

        return true;
    }

    /// <summary>
    /// O id do pagamento no nosso domínio, derivado do orçamento.
    ///
    /// Derivado, e não sorteado: o mesmo orçamento sempre produz o mesmo id, o
    /// que mantém `PagamentoConfirmado` e `PagamentoEstornado` falando do mesmo
    /// pagamento mesmo que sejam publicados por processos diferentes.
    /// </summary>
    private static string IdDoPagamento(Orcamento orcamento) => $"pag-{orcamento.Id}";
}
