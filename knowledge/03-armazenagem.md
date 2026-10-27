# POP-03 Armazenagem (putaway)

> Documento fictício · CD-01

## Objetivo
Levar a mercadoria da doca ao endereço definitivo no menor tempo, respeitando capacidade e regras de zona.

## Escolha do endereço
1. Refrigerados (ALI-) → zona C.
2. Itens de giro alto e caixas pequenas → zona B (picking).
3. Paletes e volumes grandes → zona A (porta-paletes).
4. Preferir o endereço livre mais próximo que já tenha o mesmo SKU e lote.
5. Nunca ultrapassar a capacidade cadastrada do endereço.

## Registro
A movimentação da doca para o endereço é registrada com a operação `move_stock`. Se a quantidade não couber em um endereço, dividir entre vários no mesmo plano.
