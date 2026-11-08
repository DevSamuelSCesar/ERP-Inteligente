---
nome: expedicao
descricao: Use quando for preciso criar um pedido de saída, reservar seu estoque, expedir um pedido já separado e conferido, ou cancelar um pedido que ainda não saiu.
operacoes: [create_outbound_order, reserve_stock, pick_items, ship_order, cancel_outbound_order, release_reservation]
---
# Expedição

Criar e reservar pedidos de saída e expedi-los depois de separados e conferidos. A expedição dá baixa no estoque e é crítica (POP-07 e POP-08).

## Passos
1. Criar pedido: proponha create_outbound_order com o cliente (nome exato de cliente ativo), número do pedido novo, due_on se o operador informou, e as linhas (SKU e quantidade, sem repetir SKU). Criar pedido não reserva estoque.
2. Reservar: proponha reserve_stock. A reserva segue FEFO e nunca passa do que o pedido ainda precisa. Se faltar estoque, nada é reservado e o erro lista as faltas.
3. Separar: proponha pick_items para a doca (ver skill separacao).
4. Conferência: antes de propor a expedição, confirme com quem pediu que a conferência foi concluída, com SKU, lote e quantidade iguais ao pedido (POP-07). O assistente não confere nada sozinho.
5. Expedir: proponha ship_order somente quando todas as linhas estiverem separadas por completo. Ele é crítico e irreversível.
6. Cancelar pedido que ainda não saiu: proponha cancel_outbound_order com motivo escrito. Ele libera as reservas. O estoque já separado fica na doca, sem reserva, e pode ser guardado com move_stock.
7. Só liberar reservas, mantendo o pedido: proponha release_reservation.

## Regras
- ship_order e cancel_outbound_order são críticos: quem pediu confirma, e a aprovação é de outro supervisor, que não seja quem pediu. Avise isso ao operador. O assistente nunca aprova.
- Só expedir pedido com conferência concluída. Pedido com item faltante ou excedente volta para separação. Não expeça parcialmente.
- O número do pedido de saída é único em todo o sistema. create_outbound_order não pode repetir um número existente.
- Placa do veículo e agenda são dados internos. Não peça nem registre placa.
- Cancelar o pedido não devolve ao estoque o que já foi separado sem uma movimentação própria.
- Se a validação negar por direito (expedição é de supervisor), explique quem pode fazer e não insista.

## Padrões quando faltar informação
- Número do pedido não informado: pergunte com offer_options. Não invente número.
- Cliente parecido com mais de um cadastro: pergunte qual é, com offer_options.
- due_on não informado: omita o campo.
- Reserva sem indicação de linhas: reserve tudo o que falta (sem "lines").

## Exemplo de plano
Expedir o pedido PED-FICT-5001, já separado por completo e conferido (POP-07):
```json
{"dsl_version":"1.0","intent":"ship","skill":"expedicao","steps":[{"op":"ship_order","args":{"order_number":"PED-FICT-5001"}}]}
```
