using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using PosTech.Contratos;
using PosTech.Mensageria;

namespace PosTech.Billing;

/// <summary>
/// O dinheiro, separado do orçamento. São duas coisas diferentes: um orçamento
/// aprovado pode estar sem pagamento, e um pagamento estornado não desfaz o
/// fato de o cliente ter aprovado.
/// </summary>
public enum EStatusDoPagamento
{
    SemCobranca = 0,
    Aguardando = 1,
    Confirmado = 2,
    Recusado = 3,
    Estornado = 4,
}

public enum EStatusOrcamento
{
    Pendente = 0,
    Aprovado = 1,
    Rejeitado = 2,
    Cancelado = 3,
}

/// <summary>
/// O orçamento de uma ordem, como documento.
///
/// É o agregado que justificou o Mongo (ADR-0007): itens, valores e status são
/// quase sempre lidos inteiros e raramente por partes. Em tabela, isto seriam
/// duas tabelas e um join para montar uma coisa que nunca se usa pela metade.
///
/// Repare no <see cref="EventosPendentes"/>: o outbox mora DENTRO do documento.
/// Isso não é economia de coleção, é o que torna o padrão possível aqui. Gravar
/// o orçamento e enfileirar o evento numa coleção separada seriam duas escritas,
/// e multi-documento no Mongo só é transacional com replica set. Embutido, é uma
/// escrita só — atômica por definição, em qualquer Mongo, inclusive no M0
/// gratuito do Atlas.
/// </summary>
public sealed class Orcamento
{
    [BsonId]
    [BsonRepresentation(BsonType.String)]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public int IdOrdem { get; set; }

    public int IdCliente { get; set; }

    public string NomeCliente { get; set; } = string.Empty;

    public string EmailCliente { get; set; } = string.Empty;

    public List<ItemDoOrcamento> Itens { get; set; } = [];

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Valor { get; set; }

    public EStatusOrcamento Status { get; set; } = EStatusOrcamento.Pendente;

    public string? MotivoDaRecusa { get; set; }

    public DateTime CriadoEm { get; set; }

    public DateTime? RespondidoEm { get; set; }

    public EStatusDoPagamento StatusDoPagamento { get; set; } = EStatusDoPagamento.SemCobranca;

    /// <summary>Id da cobrança no Mercado Pago. Sem ele não dá para estornar.</summary>
    public string? ReferenciaExterna { get; set; }

    /// <summary>Link que o cliente abre para pagar. Vem do adquirente.</summary>
    public string? UrlDePagamento { get; set; }

    public string? MotivoDaRecusaDoPagamento { get; set; }

    public DateTime? CobrancaCriadaEm { get; set; }

    public DateTime? PagoEm { get; set; }

    public DateTime? EstornadoEm { get; set; }

    /// <summary>
    /// Eventos aguardando publicação, gravados junto com a mudança que os
    /// originou. O publicador lê daqui e remove depois de confirmar.
    /// </summary>
    public List<EventoOutbox> EventosPendentes { get; set; } = [];
}

public sealed class ItemDoOrcamento
{
    public string Descricao { get; set; } = string.Empty;

    public ETipoItem Tipo { get; set; }

    public int Quantidade { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal ValorUnitario { get; set; }

    [BsonRepresentation(BsonType.Decimal128)]
    public decimal Subtotal { get; set; }
}
