---
nome: separacao
descricao: Use quando um pedido de saída já tiver saldo reservado e for preciso separar os itens na doca respeitando FEFO, ou quando reservas precisarem ser feitas ou liberadas.
operacoes: [reserve_stock, pick_items, release_reservation]
---
# Separação

Separar os itens de um pedido de saída que já tem saldo reservado, levando-os para a doca na ordem certa e respeitando a validade (POP-06, POL-05).

## Passos
1. Confirme o pedido com a busca de pedidos: número, situação e linhas. Se o pedido estiver "aberto", sem reserva, reserve primeiro com reserve_stock.
2. Sem "lines", reserve_stock reserva o que falta de todas as linhas. Com "lines", reserva só os SKUs indicados. Se algum SKU não tiver saldo, nada é reservado e o erro lista a falta de cada um. Informe a falta ao operador.
3. Separe com pick_items: "to" é um endereço da zona DOCA com capacidade (DOCA-01 ou DOCA-02). Sem "lines", separa toda a reserva do pedido. Com "lines", separa só os SKUs indicados, e qty limita a quantidade de cada um.
4. Para itens com validade, a reserva já segue FEFO (primeiro o lote que vence antes). Não escolha lote à mão, a menos que o operador peça. Lote vencido não é separado.
5. Em falta no endereço (o físico é menor que o reservado), não busque em outro endereço por conta própria. Registre a quantidade real e avise o supervisor.
6. Ao final, o pedido fica "em separação". Os itens seguem para a conferência (POP-07), e não para a expedição.
7. Reserva que não será usada: release_reservation libera só o que ainda não foi separado. Estoque já separado não é liberado por essa operação.

## Regras
- Reserva, separação e liberação de reserva são reversíveis, não pedem aprovação e ficam na auditoria.
- Lote com vencimento em até 7 dias sai primeiro ou deve ser encaminhado ao supervisor (POL-05).
- release_reservation não libera o que já foi separado. Para isso, use cancel_outbound_order (skill expedicao, crítica) ou move_stock.
- Não separe mais do que o pedido precisa. reserve_stock já limita a quantidade da linha.
- Quem separa não confere. A conferência é uma etapa própria, feita por outra pessoa.

## Padrões quando faltar informação
- Destino da separação: DOCA-01 ou DOCA-02, ativa com capacidade. Se o operador não disse, use DOCA-01 e liste em assumptions.
- Quantidade não informada: separe tudo o que está reservado (sem "lines").
- Pedido não citado: liste com a busca os pedidos reservados ou abertos e ofereça as opções com offer_options.

## Exemplo de plano
Separar o pedido PED-FICT-5001, já reservado, para a DOCA-01:
```json
{"dsl_version":"1.0","intent":"pick","skill":"separacao","steps":[{"op":"pick_items","args":{"order_number":"PED-FICT-5001","to":"DOCA-01"}}]}
```
