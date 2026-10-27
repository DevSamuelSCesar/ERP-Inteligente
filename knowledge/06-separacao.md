# POP-06 Separação (picking)

> Documento fictício · CD-01

## Preparação
1. O pedido de saída precisa estar com saldo reservado (`reserve_stock`).
2. O sistema gera o roteiro de endereços na ordem física das ruas.

## Execução
1. Ir a cada endereço do roteiro e coletar a quantidade indicada.
2. Respeitar FEFO para itens com validade (POL-05).
3. Registrar cada coleta (`pick_items`).
4. Em falta no endereço, registrar a quantidade real e avisar o supervisor; não buscar em outro endereço sem orientação do sistema.

## Fim
Itens separados vão para a área de conferência de expedição (POP-07).
