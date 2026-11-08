---
nome: inventario
descricao: Use quando for preciso tratar uma contagem de endereço, uma diferença de inventário, uma avaria ou um vencimento que exija ajuste ou transferência de saldo.
operacoes: [adjust_stock, move_stock]
---
# Inventário e ajustes

Tratar contagens e diferenças de saldo e propor ajustes com motivo escrito. Ajuste de estoque é operação crítica (POP-09, POP-10, POP-11 e POP-12).

## Passos
1. Antes de propor ajuste, confirme o saldo atual com get_stock, para o SKU, o endereço e o lote.
2. Em contagem cega, não mostre o saldo do sistema a quem está contando. Compare só depois da contagem.
3. Se a contagem divergir, peça uma recontagem. Só proponha ajuste quando a diferença estiver confirmada.
4. Monte a proposta: adjust_stock com sku, location, qty (diferença entre o contado e o sistema: negativa baixa, positiva acrescenta), lot quando o produto controla validade, e reason com o motivo.
5. Avaria ou vencimento: a baixa é adjust_stock negativo com o motivo correspondente. Lote vencido não é separado; vai para quarentena e a baixa é ajuste crítico.
6. Movimentação dentro do armazém, como levar um item para a área de quarentena: use move_stock. Ele não dá baixa.
7. Inventário geral: todas as movimentações ficam bloqueadas durante a contagem. Cada endereço é contado por duas equipes; divergência entre elas exige terceira contagem. Os ajustes são propostos em um único plano, um adjust_stock por endereço e lote, com o motivo "Inventário geral".

## Regras
- adjust_stock é crítico. Ele exige motivo escrito (até 200 caracteres) e é aprovado por outro supervisor, diferente de quem pediu. O assistente nunca aprova. Avise isso ao operador.
- qty nunca pode ser zero. Não proponha ajuste para "corrigir" saldo sem contagem ou motivo.
- Motivos padronizados: Avaria, Vencimento, Divergência de inventário, Erro de recebimento e Inventário geral.
- Para produto que controla validade, informe o lote no ajuste.
- Se o operador não tem permissão de ajuste, explique que o supervisor precisa propor ou aprovar, e ofereça o registro da contagem como alternativa.
- move_stock é reversível. Ajuste não é. Não use move_stock para fazer baixa.

## Padrões quando faltar informação
- Motivo não informado: pergunte com offer_options, com as cinco opções padronizadas. Não invente motivo.
- Lote não informado em produto com validade: use o lote do saldo, se houver só um naquele endereço; se houver vários, pergunte.
- Endereço não informado: pergunte. A contagem é sempre de um endereço específico.

## Exemplo de plano
Contagem cíclica de EMB-500 no endereço B-01-04: o sistema tem 28 e a recontagem confirmou 27.
```json
{"dsl_version":"1.0","intent":"adjust","skill":"inventario","steps":[{"op":"adjust_stock","args":{"sku":"EMB-500","qty":-1,"location":"B-01-04","reason":"Divergência de inventário cíclico, confirmada em recontagem"}}]}
```
