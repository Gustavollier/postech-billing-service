namespace PosTech.Contratos;

/// <summary>
/// Eventos de integração da saga da ordem de serviço.
///
/// Cada evento carrega o que o consumidor precisa para agir sozinho. Isso é
/// deliberado: se o Billing tivesse que perguntar o nome do cliente ao
/// Identidade para montar o orçamento, a geração de orçamento passaria a
/// depender do Identidade estar no ar, e o desacoplamento que a mensageria
/// comprou iria embora. Dado duplicado no evento é o preço, e é barato.
///
/// Regra para mudar qualquer um destes contratos: campo novo opcional pode
/// entrar na mesma versão; remover ou renomear campo exige versão nova, com as
/// duas versões publicadas até todos os consumidores migrarem.
/// </summary>

// ---------------------------------------------------------------------------
// Caminho feliz
// ---------------------------------------------------------------------------

[EventoDeIntegracao("OrdemAberta", RoutingKeys.OrdemAberta)]
public sealed record OrdemAberta(
    int IdOrdem,
    int IdCliente,
    string NomeCliente,
    string EmailCliente,
    int IdVeiculo,
    string DescricaoVeiculo,
    int IdFuncionario,
    DateTime AbertaEm);

/// <summary>
/// O diagnóstico terminou e os itens da ordem estão lançados.
///
/// Este evento existe por causa de uma fronteira: o valor do orçamento sai dos
/// itens, os itens pertencem ao Execução, e o Billing não pode ler o banco do
/// Execução. Em vez de uma chamada entre serviços, o Execução publica os itens
/// junto com o fato, e o Billing monta o orçamento sem perguntar nada a ninguém.
/// Ver <c>fase4/mapa-do-schema.md</c>.
/// </summary>
[EventoDeIntegracao("DiagnosticoConcluido", RoutingKeys.DiagnosticoConcluido)]
public sealed record DiagnosticoConcluido(
    int IdOrdem,
    int IdFuncionarioResponsavel,
    // O cliente vem junto porque é o Billing quem consome este evento, e ele
    // precisa do nome para o orçamento e do e-mail para o adquirente. O
    // Execução não usa nenhum dos dois: ele recebeu em OrdemAberta e repassa.
    // Carregar dado que não se usa parece desperdício até a alternativa
    // aparecer — que seria o Billing perguntar ao Identidade, e a geração de
    // orçamento passar a depender do Identidade estar no ar.
    int IdCliente,
    string NomeCliente,
    string EmailCliente,
    IReadOnlyList<ItemDaOrdem> Itens,
    decimal ValorTotal,
    DateTime ConcluidoEm);

/// <summary>
/// Um item lançado no diagnóstico, já com o valor fechado.
///
/// O preço viaja no evento em vez de ser recalculado pelo Billing: o orçamento
/// tem que valer o que valia no dia em que foi montado, e não o que a peça custa
/// hoje. Sem isso, uma mudança de tabela de preço reescreveria orçamentos já
/// enviados ao cliente.
/// </summary>
public sealed record ItemDaOrdem(
    string Descricao,
    ETipoItem Tipo,
    int Quantidade,
    decimal ValorUnitario);

public enum ETipoItem
{
    MaoDeObra = 0,
    Peca = 1,
}

[EventoDeIntegracao("OrcamentoGerado", RoutingKeys.OrcamentoGerado)]
public sealed record OrcamentoGerado(
    int IdOrdem,
    string IdOrcamento,
    decimal Valor,
    DateTime GeradoEm);

[EventoDeIntegracao("OrcamentoAprovado", RoutingKeys.OrcamentoAprovado)]
public sealed record OrcamentoAprovado(
    int IdOrdem,
    string IdOrcamento,
    decimal Valor,
    DateTime AprovadoEm);

[EventoDeIntegracao("PagamentoConfirmado", RoutingKeys.PagamentoConfirmado)]
public sealed record PagamentoConfirmado(
    int IdOrdem,
    string IdPagamento,
    decimal Valor,
    // Id da cobrança no Mercado Pago. Guardado para conciliação e para o estorno.
    string ReferenciaExterna,
    DateTime ConfirmadoEm);

[EventoDeIntegracao("ExecucaoFinalizada", RoutingKeys.ExecucaoFinalizada)]
public sealed record ExecucaoFinalizada(
    int IdOrdem,
    int IdFuncionarioResponsavel,
    DateTime FinalizadaEm);

// ---------------------------------------------------------------------------
// Falhas e compensações
//
// Estes são os eventos que o vídeo precisa mostrar. Cada um deles desfaz um
// passo que já tinha acontecido, e nenhum apaga histórico: a OS guarda a
// tentativa e o motivo, porque uma ordem que voltou atrás é informação de
// negócio, não sujeira.
// ---------------------------------------------------------------------------

[EventoDeIntegracao("OrcamentoRejeitado", RoutingKeys.OrcamentoRejeitado)]
public sealed record OrcamentoRejeitado(
    int IdOrdem,
    string IdOrcamento,
    string Motivo,
    DateTime RejeitadoEm);

[EventoDeIntegracao("PagamentoRecusado", RoutingKeys.PagamentoRecusado)]
public sealed record PagamentoRecusado(
    int IdOrdem,
    string IdPagamento,
    string Motivo,
    DateTime RecusadoEm);

/// <summary>
/// Compensa o passo 2. Publicado pelo orquestrador quando o fluxo não vai
/// seguir e o orçamento gerado precisa deixar de valer.
/// </summary>
[EventoDeIntegracao("OrcamentoCancelado", RoutingKeys.OrcamentoCancelado)]
public sealed record OrcamentoCancelado(
    int IdOrdem,
    string IdOrcamento,
    string Motivo,
    DateTime CanceladoEm);

/// <summary>
/// Compensa o passo 4. O dinheiro já tinha entrado e precisa voltar, o que
/// acontece quando a execução não assume a ordem dentro do prazo.
/// </summary>
[EventoDeIntegracao("PagamentoEstornado", RoutingKeys.PagamentoEstornado)]
public sealed record PagamentoEstornado(
    int IdOrdem,
    string IdPagamento,
    string ReferenciaExterna,
    decimal Valor,
    string Motivo,
    DateTime EstornadoEm);

/// <summary>
/// Compensa o passo 1, e fecha a saga pelo caminho triste. Todo serviço que
/// guardou algo daquela ordem escuta este evento para largar o que tiver.
/// </summary>
[EventoDeIntegracao("OrdemCancelada", RoutingKeys.OrdemCancelada)]
public sealed record OrdemCancelada(
    int IdOrdem,
    string Motivo,
    DateTime CanceladaEm);

// ---------------------------------------------------------------------------
// Topologia
// ---------------------------------------------------------------------------

/// <summary>
/// Chaves de roteamento no formato <c>contexto.fato-no-passado</c>.
///
/// O formato permite ao consumidor assinar por família sem listar evento por
/// evento: o Execução pode ligar em <c>pagamento.*</c> e passa a receber
/// confirmação e estorno sem mudança de código quando o estorno nascer.
/// </summary>
public static class RoutingKeys
{
    public const string OrdemAberta = "ordem.aberta";
    public const string OrdemCancelada = "ordem.cancelada";

    public const string DiagnosticoConcluido = "diagnostico.concluido";

    public const string OrcamentoGerado = "orcamento.gerado";
    public const string OrcamentoAprovado = "orcamento.aprovado";
    public const string OrcamentoRejeitado = "orcamento.rejeitado";
    public const string OrcamentoCancelado = "orcamento.cancelado";

    public const string PagamentoConfirmado = "pagamento.confirmado";
    public const string PagamentoRecusado = "pagamento.recusado";
    public const string PagamentoEstornado = "pagamento.estornado";

    public const string ExecucaoFinalizada = "execucao.finalizada";
}

/// <summary>
/// Nomes fixos da topologia no RabbitMQ. Um exchange do tipo topic para tudo, e
/// uma fila por serviço consumidor, cada uma com a sua dead letter.
///
/// Fila por serviço, e não por evento: é o que deixa um serviço reprocessar o
/// que é dele sem mexer na fila de ninguém, e é o que faz o painel do CloudAMQP
/// mostrar de cara qual serviço está atrasado.
/// </summary>
public static class Topologia
{
    public const string Exchange = "postech.eventos";
    public const string ExchangeDeFalhas = "postech.eventos.dlx";

    public const string FilaBilling = "billing.eventos";
    public const string FilaExecucao = "execucao.eventos";
    public const string FilaOrdemServico = "os.eventos";
    public const string FilaIdentidade = "identidade.eventos";

    public static string DeadLetterDe(string fila) => $"{fila}.dlq";

    /// <summary>
    /// O que cada fila assina. Fica aqui, e não espalhado em cada serviço, para
    /// a topologia inteira caber numa tela na hora de explicar no vídeo.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Assinaturas =
        new Dictionary<string, string[]>
        {
            // Monta o orçamento quando o diagnóstico fecha, e desfaz o que fez
            // quando o orquestrador manda. O estorno é consequência de
            // OrdemCancelada, não de um comando próprio: quem cancela a ordem
            // não precisa saber se houve pagamento.
            [FilaBilling] =
            [
                RoutingKeys.DiagnosticoConcluido,
                RoutingKeys.OrcamentoCancelado,
                RoutingKeys.OrdemCancelada,
            ],

            [FilaExecucao] =
            [
                RoutingKeys.OrdemAberta,
                RoutingKeys.PagamentoConfirmado,
                RoutingKeys.OrdemCancelada,
            ],

            // O orquestrador escuta tudo que move a saga: é por isso que a fila
            // dele é a maior, e é o preço assumido em ADR-0006.
            [FilaOrdemServico] =
            [
                RoutingKeys.DiagnosticoConcluido,
                RoutingKeys.OrcamentoGerado,
                RoutingKeys.OrcamentoAprovado,
                RoutingKeys.OrcamentoRejeitado,
                RoutingKeys.PagamentoConfirmado,
                RoutingKeys.PagamentoRecusado,
                RoutingKeys.PagamentoEstornado,
                RoutingKeys.ExecucaoFinalizada,
            ],

            // Identidade não reage a nada: ele é consultado, não coordenado.
            // A fila existe para a topologia ficar simétrica e para não precisar
            // de mudança no dia em que ele passar a escutar algo.
            [FilaIdentidade] = [],
        };
}
