---
nome: armazenagem
descricao: Use quando mercadoria recebida precisar ir da doca ou de outro endereço para o endereço definitivo, ou quando for preciso criar zonas e endereços para guardá-la.
operacoes: [move_stock, create_location, create_location_range, create_zone, update_location]
---
# Armazenagem

Levar a mercadoria da doca ao endereço definitivo no menor tempo, respeitando capacidade, zona e regras de segurança (POP-03, POP-04 e POP-15).

## Passos
1. Confirme a origem com get_stock (SKU, endereço e lote) e o destino com a busca de endereços, para ver capacidade e situação.
2. Escolha a zona pelo tipo do produto: refrigerado (SKU ALI-) vai para a zona C; giro alto e caixas pequenas vão para a zona B (picking); paletes e volumes grandes vão para a zona A.
3. Escolha o endereço livre mais próximo que já tenha o mesmo SKU e lote. Nunca passe da capacidade cadastrada e nunca use endereço desativado.
4. Se a quantidade não couber em um endereço, divida-a entre vários endereços no mesmo plano, com um move_stock para cada um.
5. Se a zona ou o endereço ainda não existir, crie-os antes no mesmo plano (create_zone, depois create_location_range ou create_location). Use "as" no passo de criação e "$nome" no passo seguinte.
6. Endereço danificado ou em manutenção: proponha update_location com is_active false e não guarde mercadoria nele. A liberação é decisão do supervisor.

## Regras
- move_stock é reversível e não pede aprovação, mas o endereço de destino precisa estar ativo e com capacidade.
- Não misture lotes diferentes do mesmo SKU no mesmo endereço sem necessidade. Informe o lot no move_stock quando o produto controla validade.
- Transferência para liberar espaço, consolidar lotes ou aproximar itens de giro alto da zona B também é move_stock.
- Move_stock não dá baixa no estoque. Perda, avaria ou vencimento são ajustes de inventário (skill inventario), que são críticos.
- Produto inflamável (por exemplo, LIM-201, álcool 70%): nunca na zona C nem perto de calor, nas posições inferiores, sem empilhar acima de 1,5 m e separado de alimentos. Em derramamento, avise o supervisor imediatamente.
- create_location_range cria os códigos ZONA-CORREDOR-NÍVEL do intervalo; nenhum código pode já existir.
- update_location: a capacidade não pode ficar abaixo da ocupação atual, e endereço com estoque não pode ser desativado.
- Se a validação negar por direito (cadastro de zona ou endereço exige permissão de supervisor ou administrador), explique isso e não insista.

## Padrões quando faltar informação
- Zona e endereço de destino: pelas regras de zona acima. Se dois endereços tiverem o mesmo critério, use o de menor código e liste em assumptions.
- Endereço novo: próximo código livre da busca de endereços na zona (next_free_codes). Capacidade: typical_capacity da zona, ou 100 se não houver.
- Quantidade "tudo" ou não informada: use o saldo disponível da origem, lido com get_stock.
- Lote não informado em produto com validade: use o lote do saldo de origem, se houver só um; se houver vários, pergunte com offer_options.

## Exemplo de plano
Guardar 40 caixas de ABC-123 da DOCA-01 no endereço A-01-02:
```json
{"dsl_version":"1.0","intent":"putaway","skill":"armazenagem","steps":[{"op":"move_stock","args":{"sku":"ABC-123","qty":40,"from":"DOCA-01","to":"A-01-02"}}]}
```
