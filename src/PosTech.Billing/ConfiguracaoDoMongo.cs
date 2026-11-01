using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace PosTech.Billing;

/// <summary>
/// Configuração de serialização do Mongo, aplicada quando o assembly carrega.
///
/// O driver 3.x recusa serializar <see cref="Guid"/> sem uma representação
/// declarada: por anos o default foi um formato binário próprio, incompatível
/// com o resto do mundo, e a correção foi obrigar a escolha. Sem isto, gravar
/// um orçamento estoura em <c>GuidSerializer.Serialize</c>, porque a MensagemId
/// de cada evento pendente é um Guid.
///
/// Standard é o UUID binário subtipo 4, que é o que outras ferramentas leem —
/// importa porque o <c>mongosh</c> e o Compass vão ser usados para mostrar o
/// documento no vídeo.
///
/// É <see cref="ModuleInitializerAttribute"/> e não um método a chamar na
/// subida do serviço de propósito: configuração que depende de alguém lembrar
/// de chamar é configuração que um dia não vai ser chamada, e o erro aparece
/// só na primeira gravação.
/// </summary>
internal static class ConfiguracaoDoMongo
{
    [ModuleInitializer]
    internal static void Aplicar()
    {
        try
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // Já registrado. Acontece quando outro assembly do mesmo processo
            // configurou antes, e não é problema: o que importa é o resultado.
        }
    }
}
