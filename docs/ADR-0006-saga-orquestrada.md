# ADR-0006 — Saga orquestrada, com o orquestrador no OS Service

**Status:** aceito
**Data:** 01/11/2026
**Contexto da fase:** Fase 4, requisito "Implementação do Saga Pattern"

## Contexto

O fluxo da ordem de serviço passa a atravessar quatro serviços com bancos
separados: abrir a OS, diagnosticar e lançar os itens, montar o orçamento,
aguardar a aprovação, cobrar, executar e fechar. Não existe transação
distribuída entre eles, e cada passo pode falhar depois que o anterior já gravou.
O enunciado aceita orquestração ou coreografia, e pede a justificativa por
escrito.

O passo de diagnóstico não estava no desenho inicial e entrou depois do
mapeamento do schema: é nele que os itens nascem, e é dos itens que sai o valor
do orçamento. Sem ele, o Billing precisaria ler o banco do Execução. Ver
`fase4/mapa-do-schema.md`.

## Decisão

Saga **orquestrada**, com o orquestrador dentro do **OS Service**.

O OS Service mantém o estado da saga junto da própria ordem, decide qual é o
próximo passo e dispara a compensação quando um passo falha. Os outros três
serviços não sabem que existe uma saga: eles consomem o evento que lhes diz
respeito, fazem o seu trabalho e publicam o resultado.

## Por que não coreografia

Coreografia é mais idiomática em microsserviços e teria menos acoplamento. Duas
razões pesaram mais.

**A primeira é de demonstração.** O vídeo precisa mostrar a saga executando e
tratando falha. Com orquestração existe um lugar único onde o estado aparece e
onde a compensação é disparada, e esse lugar pode ser aberto na tela. Com
coreografia, a mesma explicação vira o rastro de cinco serviços reagindo uns aos
outros, que é verdade mas não se filma em três minutos.

**A segunda é de reaproveitamento.** O `OrdemServicoDomainService` já é uma
máquina de estados com transições estritamente sequenciais, escrita e testada na
Fase 2. O orquestrador não é código novo conceitualmente: é essa máquina
coordenando passos que passaram a morar fora do processo. Escolher coreografia
jogaria essa regra fora e a espalharia entre quatro serviços.

O custo é real e assumido: o OS Service vira o ponto que conhece o fluxo inteiro,
e um passo novo na saga mexe nele. Para um fluxo de cinco passos que muda pouco,
é um custo baixo.

## Como a compensação funciona

Cada passo tem o seu inverso, e nenhum deles apaga histórico. Uma ordem que
voltou atrás é informação de negócio, então a OS guarda a tentativa e o motivo.

| Falha | Compensação | Evento |
| --- | --- | --- |
| Pagamento recusado | Cancela o orçamento e devolve a OS para Aguardando Aprovação | `OrcamentoCancelado` |
| Execução não assume no prazo | Estorna o pagamento e cancela a ordem | `PagamentoEstornado`, `OrdemCancelada` |
| Falha ao publicar | Nenhuma: o evento fica no outbox e é reenviado | — |

O terceiro caso é o que torna os dois primeiros confiáveis. Gravar no banco e
publicar no broker são recursos diferentes e não cabem numa transação só; o
outbox transacional resolve isso gravando o evento na mesma transação do negócio
e deixando a entrega para um processo à parte. É por isso que a compensação pode
ser disparada por evento sem risco de a ordem mudar de estado e o aviso se perder.

## Consequências

- O estado da saga vive em `os-db`, ao lado da ordem, e não num serviço separado.
- Os eventos de compensação são contratos públicos como os outros, versionados do
  mesmo jeito (ver `fase4/contratos/Eventos.cs`).
- Idempotência é obrigatória nos consumidores: o broker entrega ao menos uma vez,
  e uma compensação aplicada duas vezes estorna duas vezes. A tabela
  `EventoProcessado` existe para isso.
- O teste que força a recusa de pagamento e verifica a compensação é o critério
  de pronto da Etapa 3, e é o roteiro do trecho mais difícil do vídeo.
