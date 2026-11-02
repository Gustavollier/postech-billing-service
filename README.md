# postech-billing-service

[![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-billing-service&metric=alert_status)](https://sonarcloud.io/summary/new_code?id=postech4_postech-billing-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-billing-service&metric=coverage)](https://sonarcloud.io/summary/new_code?id=postech4_postech-billing-service) [![Bugs](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-billing-service&metric=bugs)](https://sonarcloud.io/summary/new_code?id=postech4_postech-billing-service) [![Vulnerabilidades](https://sonarcloud.io/api/project_badges/measure?project=postech4_postech-billing-service&metric=vulnerabilities)](https://sonarcloud.io/summary/new_code?id=postech4_postech-billing-service) [![CI/CD](https://github.com/Gustavollier/postech-billing-service/actions/workflows/ci-cd.yml/badge.svg)](https://github.com/Gustavollier/postech-billing-service/actions/workflows/ci-cd.yml)

Microsserviço de **Orçamento e Pagamento** do Tech Challenge Fase 4 (FIAP 13SOAT).

Dono do orçamento, da aprovação do cliente e da cobrança no adquirente.


> Visão de conjunto dos quatro serviços, com o diagrama da arquitetura e da saga: [`docs/arquitetura.md`](docs/arquitetura.md).

## O que este serviço faz

| Responsabilidade | Detalhe |
| --- | --- |
| Montar o orçamento | A partir dos itens que vieram no `DiagnosticoConcluido`, recalculando o total |
| Receber a resposta do cliente | Aprovar ou rejeitar, cada uma publicando o seu evento |
| Cobrar | Cria a cobrança no Mercado Pago na aprovação |
| Confirmar o pagamento | Pelo webhook do adquirente — ou já na aprovação, quando o sandbox responde na hora |
| Compensar | Cancela o orçamento e estorna o pagamento quando a saga manda |

Banco: **MongoDB** (`billing-db`). Mensageria: **RabbitMQ**.

## Por que Mongo aqui

O requisito da fase é ter pelo menos um banco NoSQL, e o orçamento é o
agregado que mais justifica: ele é lido e escrito **inteiro**, os itens não têm
vida própria fora dele, e o formato muda conforme o que o diagnóstico trouxe.
Em tabela, isso seria `Orcamento` + `ItemDoOrcamento` + join a cada leitura
para montar algo que nunca é consultado em pedaços.

A justificativa completa está em
[`docs/ADR-0007-banco-por-servico.md`](docs/ADR-0007-banco-por-servico.md).

### O outbox mora dentro do documento

No OS Service o outbox é uma tabela e a atomicidade vem da transação. Aqui ela
vem de graça: **gravar o orçamento e enfileirar o evento é uma escrita de
documento só**, e escrita de um documento é atômica por definição no Mongo.

Isso foi decisão de desenho, não acidente: transação multi-documento no Mongo
exige replica set, e o **M0 gratuito do Atlas** — que é o plano que cabe no
orçamento — não entrega isso de forma confiável para o que precisamos. Com o
outbox embutido, não precisamos de transação nenhuma.

O `ProcessadorDoOutbox` é o mesmo código que roda sobre Azure SQL no OS
Service. Ele não sabe onde o dado mora, e existe um teste que prova isso.

## O que o serviço consome e publica

```
consome                      publica
---------------------------  -------------------------------
DiagnosticoConcluido     ->  OrcamentoGerado
(HTTP) aprovar           ->  OrcamentoAprovado, PagamentoConfirmado
(HTTP) rejeitar          ->  OrcamentoRejeitado
(webhook) pagamento      ->  PagamentoConfirmado | PagamentoRecusado
OrcamentoCancelado       ->  —
OrdemCancelada           ->  PagamentoEstornado (só se houve pagamento)
```

O `DiagnosticoConcluido` carrega os itens **e os dados do cliente**. É dado
duplicado de propósito: sem ele, montar orçamento passaria a depender do
Execução e do Identidade estarem no ar — e um serviço que precisa de outros dois
de pé para fazer o seu trabalho não é um microsserviço, é um módulo com
latência de rede.

## Pagamento

O fluxo tem três pontos que valem explicar.

**A ordem na aprovação.** Primeiro grava a aprovação com o `OrcamentoAprovado`
enfileirado na mesma escrita; só então cria a cobrança. Cobrar antes de
registrar deixaria cobrança órfã se o banco falhasse em seguida.

**Idempotência na cobrança.** A chamada ao Mercado Pago vai com
`X-Idempotency-Key` igual ao id do orçamento. Dois cliques em "pagar" não geram
duas cobranças.

**O webhook não é tratado como verdade.** A rota é pública — qualquer um pode
fazer um POST nela. A notificação diz apenas que algo mudou; quem diz o que
aconteceu é a consulta de volta ao adquirente. E a resposta é 200 mesmo quando
não há nada a fazer, porque o Mercado Pago reenvia até receber 2xx e devolver
erro para notificação já processada vira tempestade de reenvio.

**Sem token configurado o serviço sobe.** Em vez de estourar na partida, entra
um gateway que recusa toda cobrança dizendo o que falta. O resto do Billing —
montar orçamento, consumir a fila, compensar — continua funcionando.

## Rotas

| Método | Rota | O quê |
| --- | --- | --- |
| `GET` | `/api/v1/orcamentos/os/{idOrdem}` | O orçamento da ordem, com itens e status do pagamento |
| `POST` | `/api/v1/orcamentos/os/{idOrdem}/aprovar` | Cliente aprova; cria a cobrança |
| `POST` | `/api/v1/orcamentos/os/{idOrdem}/rejeitar` | Cliente rejeita, com motivo |
| `POST` | `/api/v1/pagamentos/webhook` | Notificação do Mercado Pago |
| `GET` | `/health` · `/health/ready` | Liveness e readiness |

Swagger em `/swagger`.

## Rodar local

```bash
# Infraestrutura (RabbitMQ, Mongo, SQL Server)
docker compose -f docker-compose.yml up -d

# API
export MongoConnectionString="mongodb://postech:senha@localhost:27017/?authSource=admin"
export RabbitMqConnectionString="amqp://postech:senha@localhost:5672/"
# Opcional: sem ele a cobrança fica indisponível e o resto funciona
export MercadoPago__AccessToken="TEST-..."
dotnet run --project src/PosTech.Billing.Api
```

Os índices do `billing-db` são criados na partida, de forma idempotente — não
há script de schema a rodar à mão.

## Testes

```bash
dotnet test
```

**73 testes, 90% de cobertura de linha.** Os de integração sobem MongoDB e
RabbitMQ de verdade via Testcontainers — precisa de Docker. Dublê não prova
atomicidade de escrita nem índice único, que é justamente o que mais importa
aqui: o índice por ordem é o que impede duas entregas do mesmo
`DiagnosticoConcluido` virarem dois orçamentos.

O grupo mais importante é o de `FluxoDoBillingTests`: o serviço **inteiro no
ar**, com o evento entrando pelo RabbitMQ, o orçamento nascendo no Mongo, o
cliente respondendo por HTTP, e o evento de saída atravessando o broker até a
fila do orquestrador. O único dublê é o adquirente, porque o Mercado Pago não é
parte do nosso sistema.

Isso achou três coisas que teste de unidade não acharia:

1. **O serviço não subia.** Compilava, 35 testes passavam, e o host morria na
   partida — faltava registrar `IRegistroDeIdempotencia` no contêiner de
   injeção. Montar o grafo de dependências é comportamento do serviço e passou a
   ser exercitado como tal.
2. **`RegistrarCobranca` não marcava o pagamento como `Aguardando`.** O
   orçamento ficava com referência do adquirente e URL de pagamento, mas
   reportando `SemCobranca` — indistinguível de quem nunca foi cobrado, que é
   exatamente a diferença que o cliente precisa ver na tela. Havia até um teste
   afirmando `SemCobranca`: o defeito estava escrito como se fosse regra.
3. **O `mongo:8` não sobe no runner do GitHub**, que roda kernel 6.19
   ([SERVER-121912](https://jira.mongodb.org/browse/SERVER-121912)). Passava
   local, falhava no CI. O contorno está em `InfraDeTeste`, num lugar só.

O que os testes de regra garantem, entre outras coisas:

- o valor sai da soma dos subtotais, e não do total que veio no evento
- o preço fica congelado no documento: mudança na tabela de peças não reescreve
  orçamento já enviado
- cancelar, confirmar, recusar e estornar são todos idempotentes — evento chega
  ao menos uma vez
- estornar o que nunca foi pago não inventa movimento financeiro
- decimal sobrevive à ida e volta no Mongo com os centavos intactos
  (`Decimal128`, não `double`)

A pipeline **reprova abaixo de 80% de cobertura**.

## Configuração

| Variável | O quê |
| --- | --- |
| `MongoConnectionString` | Conexão com o `billing-db`. Obrigatória |
| `RabbitMqConnectionString` | Conexão AMQP. Obrigatória |
| `MongoDatabase` | Nome do banco. Padrão `billing-db` |
| `MercadoPago__AccessToken` | Token do adquirente. **Opcional** — sem ele a cobrança fica indisponível |
| `MercadoPago__BaseUrl` | Padrão `https://api.mercadopago.com` |

As duas primeiras são obrigatórias e o serviço **se recusa a subir sem elas** —
falhar na partida é melhor que descobrir na primeira requisição.

## Documentação

- [ADR-0006 — Saga orquestrada](docs/ADR-0006-saga-orquestrada.md)
- [ADR-0007 — Um banco por serviço](docs/ADR-0007-banco-por-servico.md)
- [ADR-0008 — CQRS só como separação de código](docs/ADR-0008-cqrs-sem-infraestrutura-nova.md)
