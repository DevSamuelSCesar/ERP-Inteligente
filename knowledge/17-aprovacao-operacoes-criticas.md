# POL-17 Aprovação de operações críticas

> Documento fictício · CD-01

## Operações críticas
Ajuste de estoque, expedição (baixa), exclusão de cadastros e qualquer operação em massa.

## Regras
- Só um usuário com papel de supervisor pode aprovar.
- O solicitante nunca aprova o próprio pedido (segregação de funções).
- O assistente de IA não aprova nada; ele só prepara o plano e mostra o efeito previsto.
- Toda aprovação ou rejeição fica registrada na auditoria.

## Operações reversíveis
Recebimento, transferência, reserva e separação executam sem aprovação, mas também ficam na auditoria e podem ser desfeitas por movimento compensatório.
