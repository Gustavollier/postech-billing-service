# ADR-0008 — CQRS só como separação de código, sem tabela nova

**Status:** aceito
**Data:** 01/11/2026
**Revisado:** 01/11/2026, decisão do líder técnico

## Contexto

A pergunta veio com a restrição junto: usar CQRS se fizer sentido, sem gastar
crédito, e **sem criar tabela que não esteja mapeada**.

A primeira versão deste ADR propunha um read model `OrdemResumo` em `os-db`,
alimentado pelos eventos, para a tela de detalhe da ordem não depender de três
chamadas entre serviços. Custava zero em infraestrutura, mas custava uma tabela
nova — e tabela nova está fora do combinado.

## Decisão

**CQRS apenas como separação de comando e consulta dentro de cada serviço.
Nenhuma tabela a mais, nenhum banco a mais, nenhum processo a mais.**

Na prática:

- **Escrita** passa pelo caso de uso e pelo modelo de domínio, com as
  invariantes validadas no agregado.
- **Leitura** vai direto do repositório para o DTO, sem carregar agregado e sem
  passar por caso de uso.

O monólito já fazia isso informalmente — as consultas em Dapper devolvem DTO
direto, as escritas passam pelos use cases. Aqui vira regra escrita, e continua
custando nada.

**Fora de escopo:** read model desnormalizado, banco de leitura separado,
projeções e event sourcing.

## Como a tela da ordem fica sem o read model

Boa parte do que a tela precisa já vem de graça, porque a **tabela de estado da
saga é obrigatória de qualquer forma** (requisito do Saga Pattern, já mapeado no
plano) e ela carrega o valor, o id do orçamento e o id do pagamento.

| O que a tela mostra | De onde vem | Custo |
| --- | --- | --- |
| Status e histórico | `OrdemServico` e `Status`, em `os-db` | já existe |
| Valor do orçamento | estado da saga, em `os-db` | já é obrigatório |
| Id do orçamento e do pagamento | estado da saga | já é obrigatório |
| Nome do cliente e veículo | chamada REST ao Identidade | 1 chamada |
| Itens lançados | chamada REST ao Execução | 1 chamada |

Sobram **duas chamadas entre serviços** na tela de detalhe, e nenhuma na
listagem — que mostra status, data e valor, tudo de `os-db`.

## Consequências

- Duas chamadas a mais no detalhe de uma ordem. Em escala de demonstração isso
  não aparece, e a listagem, que é a tela mais usada, não paga nada.
- A tela de detalhe passa a depender de Identidade e Execução estarem no ar.
  Degradar bem é responsabilidade dela: sem resposta, mostra o que tem de
  `os-db` em vez de falhar inteira.
- O documento de entrega vai dizer exatamente isto: **separação de comando e
  consulta no código, sem read model**. Chamar de CQRS completo seria vender
  mais do que foi feito, e a banca pergunta justamente pelo banco de leitura
  separado que não existiria.
- Se o volume um dia pedir, o read model entra sem reescrever nada: os eventos
  que o alimentariam já são publicados e já são consumidos pelo orquestrador.
