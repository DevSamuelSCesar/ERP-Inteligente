# Canvas: Nexus (módulo de armazém)

| | |
|---|---|
| **Problema e usuário** | Operadores e supervisores de armazéns pequenos e médios, sem equipe de TI. Hoje usam planilhas ou sistemas com muitas telas, que exigem treinamento e geram erros de digitação, estoque divergente e produtos vencidos. **Quando funciona:** o operador pede em português ("guarde as 40 caixas do ABC-123 que estão na doca") e o sistema consulta, planeja, mostra o efeito e executa com segurança. Os erros caem, o treinamento encurta e o supervisor só decide o que é crítico. |
| **Entrada** | Pedido em linguagem natural (texto no app desktop); dados do armazém no SQLite; procedimentos em Markdown (RAG); skills (`SKILL.md` + `skill.json`). |
| **Saída** | Plano JSON validado e simulado; movimentação de estoque gravada em transação; resposta em linguagem simples; pedido de confirmação para operações críticas; registro na auditoria; novas memórias e receitas. |
| **Maior risco** | A IA executar uma alteração errada ou não autorizada no estoque, por alucinação ou prompt injection. **Mitigação:** a IA não escreve no banco; só envia planos que o código valida, simula e executa em transação. Os direitos da IA são a interseção com os do usuário, operações críticas exigem um supervisor, e a conexão da IA é somente leitura. |
| **MVP em 3 semanas** | **Semana 1:** schema, segurança e carga no SQLite (pronto em `db/`); projeto Avalonia com login e tela de chat. **Semana 2:** motor de planos (Validator, Simulator, Executor) com `receive_goods` e `move_stock`; ferramentas de leitura; Planner com Claude Sonnet 5.5; skills `recebimento` e `armazenagem`. **Semana 3:** RAG sobre `knowledge/` (FTS5 + `sqlite-vec`), aprovação de plano crítico, auditoria encadeada, suíte de ataques de prompt injection. **Fica fora do MVP:** receitas automáticas, fine-tuning, inventário e expedição. |

## Arquitetura em caixas

```
┌────────────────────┐
│ Operador / Superv. │
└─────────┬──────────┘
          │ pedido em português
┌─────────▼──────────┐      ┌──────────────────────────┐
│ App desktop        │      │ LLM (Claude Sonnet 5.5 / │
│ Avalonia: chat,    │◄────►│ Haiku 5.5; DeepSeek e    │
│ prévia, aprovação  │      │ OpenAI configuráveis)    │
└─────────┬──────────┘      └────────────▲─────────────┘
          │                              │ contexto sem dados sensíveis
┌─────────▼──────────────────────────────┴─────────────┐
│ Camada de IA: Planner · skills · memórias · RAG      │
│ ferramentas de leitura (conexão somente leitura)     │
└─────────┬────────────────────────────────────────────┘
          │ propose_plan (plano JSON)
┌─────────▼────────────────────────────────────────────┐
│ Motor determinístico: Validator → Simulator →        │
│ Aprovação (crítico) → Executor (transação)           │
└─────────┬────────────────────────────────────────────┘
┌─────────▼────────────────────────────────────────────┐
│ SQLite + SQLCipher: domínio · planos · receitas ·    │
│ memórias (FTS5) · RAG (sqlite-vec) · auditoria       │
└──────────────────────────────────────────────────────┘
```
