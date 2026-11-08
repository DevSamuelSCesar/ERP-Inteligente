---
nome: recebimento
descricao: Use quando mercadoria chegar na doca para registrar a entrada, conferir quantidades contra o pedido de entrada ou tratar divergências.
operacoes: [create_inbound_order, receive_goods, cancel_inbound_order]
---
# Recebimento

Registrar a entrada de mercadoria na doca, conferindo quantidade e integridade contra o pedido de entrada ou a nota fiscal (POP-01 e POP-02).

## Passos
1. Identifique o pedido de entrada pelo número do documento (por exemplo, NF-FICT-000102) com a busca de pedidos. Se ele não existir, proponha create_inbound_order com fornecedor ativo, número do documento e as linhas esperadas (SKU e quantidade).
2. Confirme a doca de destino: DOCA-01 ou DOCA-02, ativa e com espaço livre.
3. Para cada SKU, use receive_goods com a quantidade efetivamente conferida, o endereço da doca e o número do documento em inbound_order.
4. Produto que controla validade: informe lot e expires_on de cada lote recebido.
5. Divergência: receba só o que foi conferido. Falta e sobra são anotadas com o tipo e a quantidade. Sobra e item trocado ficam separados na doca até o supervisor decidir (prazo de 24 horas). Avaria vai para quarentena (POP-12), nunca para o saldo disponível.
6. Pedido de entrada lançado por engano e ainda sem nenhum recebimento: proponha cancel_inbound_order com motivo escrito.

## Regras
- Registre apenas a quantidade contada. Nunca registre a quantidade esperada como se fosse conferida.
- Refrigerados (SKUs ALI-) não podem ficar mais de 30 minutos na doca. Proponha, junto com o recebimento, a armazenagem para a zona C (skill armazenagem) ou avise que ela precisa ser feita logo.
- Volume com embalagem violada, amassado, molhado, aberto ou vencido vai para quarentena, sem saldo disponível para venda.
- Número do documento de create_inbound_order não pode repetir outro pedido de entrada do armazém. O fornecedor precisa estar ativo, com nome exato.
- cancel_inbound_order é operação crítica: só serve para pedido em situação "esperado" (nada recebido). Ela vai para outro supervisor aprovar depois da sua confirmação; avise isso ao operador.
- Recebimento e criação de pedido de entrada são reversíveis e não pedem aprovação, mas ficam na auditoria.
- Sobra ou item trocado não gera ajuste de estoque sem decisão do supervisor.

## Padrões quando faltar informação
- Doca: DOCA-01 ou DOCA-02, ativa com espaço livre. Se houver memória de doca preferida para o fornecedor, use essa.
- Lote, quando o operador não informou: {AAAAMMDD de hoje}-SKU, por exemplo 20261108-ALI-301. Se a nota trouxer o lote, use o da nota.
- Validade, quando não informada: assuma, liste em assumptions e peça confirmação no chat. Se não houver base alguma, pergunte com offer_options.
- Quantidade não informada: pergunte. Não assuma que a quantidade esperada foi conferida.
- Documento não encontrado: tente outros termos da busca antes. Se nada aparecer, proponha create_inbound_order como suposição explícita.

## Exemplo de plano
Recebimento de 20 unidades de ALI-301 do pedido NF-FICT-000102 na DOCA-02, com lote da data de hoje e validade assumida:
```json
{"dsl_version":"1.0","intent":"receive","skill":"recebimento","steps":[{"op":"receive_goods","args":{"sku":"ALI-301","qty":20,"location":"DOCA-02","lot":"20261108-ALI-301","expires_on":"2026-12-10","inbound_order":"NF-FICT-000102"}}]}
```
