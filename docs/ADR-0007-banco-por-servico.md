# ADR-0007 — Um banco por serviço, dois motores, servidor lógico compartilhado

**Status:** aceito
**Data:** 01/11/2026
**Contexto da fase:** Fase 4, requisitos "banco de dados próprio por serviço",
"pelo menos um SQL e um NoSQL" e "nenhum serviço acessa diretamente o banco de outro"

## Contexto

Cada microsserviço precisa do próprio banco, com pelo menos um relacional e pelo
menos um não relacional no conjunto. Hoje tudo compartilha uma base Azure SQL, e
a Auth Function lê a tabela `Cliente` por conexão direta — o que era uma decisão
justificada na Fase 3 e passa a ser violação explícita nesta.

A restrição que molda a decisão é de custo: a conta é estudantil, com US$ 100
para a fase inteira, e cada Azure SQL Basic custa cerca de US$ 4,90 por mês.

## Decisão

| Serviço | Banco | Motor |
| --- | --- | --- |
| Identidade | `identidade-db` | Azure SQL Basic |
| Ordem de Serviço | `os-db` | Azure SQL Basic |
| Billing | `billing-db` | MongoDB Atlas M0 |
| Execução | `execucao-db` | MongoDB Atlas M0 |

Os dois bancos SQL ficam no **mesmo servidor lógico**, o
`postech-sql-13soat-cus`, cada um com login e permissão próprios e sem acesso ao
outro. Os dois bancos Mongo ficam no mesmo cluster M0, com usuários distintos.

## Por que o NoSQL fica no Billing e no Execução

Orçamento é um agregado fechado: itens, valores e status, quase sempre lidos
inteiros e raramente por partes. Cabe em documento com naturalidade, e o retorno
do Mercado Pago reforça, porque o webhook devolve JSON que fica desconfortável em
tabela. A fila de execução tem a mesma forma.

O ganho decisivo é de risco, não de modelagem: manter o **OS Service em SQL**
preserva a parte mais complexa e mais testada do domínio, com as queries Dapper e
o schema que já existem, e concentra a persistência nova nos dois serviços que
iam nascer do zero de qualquer forma.

## Por que servidor lógico compartilhado

O requisito é banco de dados próprio, não servidor próprio. No Azure SQL, o
servidor lógico não custa nada: a cobrança é por database. Dois servidores
dobrariam o trabalho de firewall, identidade e Terraform sem melhorar isolamento
nenhum, porque o isolamento que o enunciado pede é de **acesso**, e esse vem do
login por banco.

O isolamento é garantido assim:

- cada serviço tem o seu próprio login, com permissão apenas no banco dele;
- nenhuma query faz `JOIN` entre bancos, e nenhuma connection string de um
  serviço aparece na configuração de outro;
- o que antes era `JOIN` entre contextos virou dado carregado no evento ou
  chamada REST ao dono.

## Consequências

- **A Auth Function deixa de violar a regra**: ela passa a ser parte do contexto
  de Identidade e lê `identidade-db`, que é o banco dela. A decisão da Fase 3
  continua válida; o que muda é de quem é a tabela.
- Dado duplicado passa a existir de propósito. O Billing guarda nome e e-mail do
  cliente que vieram no `OrdemAberta` em vez de perguntar ao Identidade. É
  consistência eventual assumida, e é o que impede a geração de orçamento de
  depender do Identidade estar no ar.
- O custo em SQL fica em cerca de US$ 9,80 por mês, e os dois bancos Mongo não
  consomem crédito nenhum.
- Se a banca questionar o servidor compartilhado, a resposta é este ADR: foi
  decisão registrada, com o isolamento garantido por credencial, não atalho.
