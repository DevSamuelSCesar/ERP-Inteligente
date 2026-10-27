# POP-01 Recebimento de mercadorias

> Documento fictício · CD-01

## Objetivo
Registrar a entrada de mercadorias na doca, conferindo quantidade e integridade contra o pedido de compra ou nota fiscal.

## Passos
1. Identificar o pedido de entrada pelo número do documento (ex.: NF-FICT-000102).
2. Descarregar na doca indicada (DOCA-01 ou DOCA-02).
3. Contar volumes por SKU e comparar com a quantidade esperada.
4. Para produtos com validade, registrar lote e data de validade de cada volume.
5. Registrar o recebimento no sistema (operação `receive_goods`).
6. Divergências seguem o POP-02.

## Regras
- Produtos refrigerados (prefixo ALI-) não podem ficar mais de 30 minutos na doca.
- Volume com embalagem violada vai para quarentena (POP-12), nunca para o estoque disponível.
