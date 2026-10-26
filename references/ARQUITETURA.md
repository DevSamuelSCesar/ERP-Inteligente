# Arquitetura

> Escopo atual: módulo de **armazém (WMS)**. Stack definida em [`STACK.md`](STACK.md).

## 1. Princípio central

**O LLM descreve as alterações; o código as executa.**

O LLM usa tool calling o tempo todo, mas de duas formas diferentes:

- **Ferramentas de leitura** (`get_stock`, `find_location`, `search_recipes`...) executam na hora e devolvem dados ao LLM.
- **Alterações de dados** nunca acontecem por chamadas individuais. O LLM monta um **plano JSON** com todos os passos e o envia por uma única ferramenta, `submit_plan`. Um motor determinístico, sem LLM, valida o plano, simula o efeito e o executa numa única transação: aplica tudo ou nada.

Assim, a consistência dos dados é garantida pelo código (validação + transação) e não depende do raciocínio do modelo durante a execução.

---

## 2. Premissas núcleo

| # | Premissa | Como a arquitetura atende |
|---|----------|---------------------------|
| P1 | Pouca intervenção manual do operador | O LLM planeja de ponta a ponta. O humano só confirma planos de risco crítico. |
| P2 | Pouca necessidade de conhecimento técnico | O operador conversa em linguagem natural e vê uma pré-visualização do plano em termos do negócio. |
| P3 | Automação confiável via tool calling | Ferramentas tipadas para leitura; escrita só por planos validados e transacionais. |
| P4 | Sistema de skills do ERP | Os procedimentos do armazém ficam em skills (Markdown + JSON) carregadas sob demanda. |
| P5 | Eficiência de tokens | Contexto mínimo por tarefa, ferramentas filtradas pela skill e **receitas** que rodam sem LLM. |
| P6 | RAG quando houver vantagem | Só sobre texto livre: memórias, skills e receitas. Dados estruturados são lidos por consultas tipadas. |
| P7 | Melhoria contínua | Memórias e receitas são gravadas em SQLite e consultadas nas próximas tarefas. |

---

## 3. Visão geral

```
┌──────────────────────────────────────────────────────────────┐
│ UI (Avalonia, MVVM)                                          │
│  chat · pré-visualização do plano · confirmação · consultas  │
└───────────────┬──────────────────────────────────────────────┘
                │ pedido em linguagem natural
┌───────────────▼──────────────────────────────────────────────┐
│ Recipe Matcher ── existe receita para este pedido?           │
│   sim → LLM barato só preenche parâmetros (ou nenhum LLM)    │
│   não → Planner                                              │
└───────────────┬──────────────────────────────────────────────┘
┌───────────────▼──────────────────────────────────────────────┐
│ Planner (LLM)                                                │
│  skill ativa · memórias relevantes · ferramentas de leitura  │
│  → submit_plan(plano JSON)                                   │
└───────────────┬──────────────────────────────────────────────┘
                │ a partir daqui, sem LLM
┌───────────────▼──────────────────────────────────────────────┐
│ Validator ── JSON Schema + regras de negócio                 │
│   inválido → erro estruturado devolvido ao Planner           │
├──────────────────────────────────────────────────────────────┤
│ Simulator (dry-run) ── calcula o diff de estoque e o risco   │
├──────────────────────────────────────────────────────────────┤
│ Approval ── risco crítico → confirmação do operador          │
├──────────────────────────────────────────────────────────────┤
│ Executor ── transação SQLite (tudo ou nada)                  │
└───────────────┬──────────────────────────────────────────────┘
┌───────────────▼──────────────────────────────────────────────┐
│ Learning ── grava memórias · promove planos a receitas       │
└──────────────────────────────────────────────────────────────┘
        Persistência: SQLite (domínio, planos, receitas, memórias, auditoria)
```

---

## 4. Fluxo de uma tarefa

1. **Pedido:** o operador escreve, por exemplo, "Guarde as 40 caixas do ABC-123 que estão na doca".
2. **Receita:** o Recipe Matcher procura uma receita compatível (seção 7). Se encontrar, pula para o passo 6 com o plano gerado a partir dela.
3. **Skill:** o Planner escolhe a skill pelo índice (nome + descrição) e só então carrega o `SKILL.md` completo.
4. **Contexto:** entram no contexto as memórias relevantes (top-k pequeno, filtrado por escopo) e as ferramentas de leitura permitidas pela skill.
5. **Planejamento:** o LLM consulta o estado com ferramentas de leitura e chama `submit_plan`.
6. **Validação:** se o plano for inválido, o erro volta ao LLM em formato estruturado para ser corrigido, com limite de tentativas. Ao atingir o limite, a tarefa é escalada ao operador.
7. **Simulação:** o dry-run calcula o efeito (o que entra, o que sai e o que muda de endereço) e o nível de risco do plano.
8. **Aprovação:** um plano de risco crítico é mostrado ao operador em linguagem de negócio para confirmação. Os demais seguem direto.
9. **Execução:** o Executor aplica todos os passos numa transação. Se qualquer passo falhar, nada é gravado.
10. **Aprendizado:** o resultado é registrado; memórias e receitas são atualizadas (seções 7 e 8).

### Exemplo

```
LLM → find_location({ sku: "ABC-123", qty: 40 })      ← leitura, executa na hora
    ← [{ location: "A-03-02", free: 20 }, { location: "A-03-04", free: 30 }]
LLM → submit_plan({ ...plano... })
    ← { status: "executed", plan_id: 812, summary: "40 cx ABC-123: DOCA-01 → A-03-02 (20), A-03-04 (20)" }
```

---

## 5. Plano JSON (DSL)

```json
{
  "intent": "putaway",
  "skill": "armazenagem",
  "steps": [
    { "op": "move_stock", "args": { "sku": "ABC-123", "qty": 20, "from": "DOCA-01", "to": "A-03-02" } },
    { "op": "move_stock", "args": { "sku": "ABC-123", "qty": 20, "from": "DOCA-01", "to": "A-03-04" } }
  ]
}
```

Regras da DSL:

- `op` pertence a um catálogo fechado de **operações de escrita**. Cada operação tem um schema de argumentos, regras de validação e um nível de risco.
- Um passo pode guardar seu resultado com `"as": "nome"`, e passos seguintes podem referenciá-lo com `"$nome"`.
- Não há laços nem condicionais arbitrárias. O que exigir lógica complexa vira uma nova operação no catálogo, implementada em C# e testada.
- A DSL é **versionada**. Planos e receitas guardam a versão com que foram criados.

### Catálogo inicial de operações (armazém)

| Operação | Descrição | Risco |
|----------|-----------|-------|
| `receive_goods` | Registra a entrada de itens na doca | Reversível |
| `move_stock` | Transfere saldo entre endereços | Reversível |
| `reserve_stock` | Reserva saldo para um pedido de saída | Reversível |
| `pick_items` | Registra a separação de itens | Reversível |
| `ship_order` | Expede e baixa o estoque | Crítico |
| `adjust_stock` | Ajusta saldo por inventário ou avaria (exige motivo) | Crítico |
| `create_location` / `create_product` | Cadastros | Reversível |

O risco do plano é o **maior risco** entre seus passos.

---

## 6. Ferramentas expostas ao LLM

| Ferramenta | Tipo | Função |
|------------|------|--------|
| Consultas de domínio (`get_stock`, `find_location`, `get_order`, `list_pending_receipts`...) | Leitura | Respostas compactas e paginadas |
| `search_memories` | Leitura | Busca memórias além das já injetadas no contexto |
| `search_recipes` | Leitura | Busca receitas similares para usar como base |
| `load_skill` | Leitura | Carrega o `SKILL.md` de uma skill |
| `submit_plan` | Escrita (via motor) | Única porta de entrada para alterar dados |
| `remember` | Memória | Registra uma memória candidata (seção 8) |

A skill ativa define quais consultas e quais operações do catálogo ficam disponíveis.

---

## 7. Receitas

Uma **receita** é um plano parametrizado que já funcionou. É o principal mecanismo de economia de tokens: com o uso, cada vez mais tarefas rodam sem planejamento pelo LLM.

- **Promoção:** um plano é candidato a receita quando a mesma estrutura de passos é executada com sucesso repetidas vezes, sem correção do operador. Os valores variáveis (SKU, quantidade, endereço) viram parâmetros.
- **Uso:**
  - Disparo direto pela UI (botão ou atalho) com parâmetros preenchidos pelo operador: **nenhum LLM**.
  - Disparo por linguagem natural: um modelo barato só identifica a receita e extrai os parâmetros.
- **Validação:** o plano gerado a partir de uma receita passa pelo mesmo Validator, Simulator e Approval de qualquer plano.
- **Invalidação:** a receita guarda a versão da DSL e da skill. Se uma das duas mudar, a receita fica suspensa até ser revalidada. Receitas que falham repetidamente são despromovidas.

---

## 8. Memória e melhoria contínua

Tabela `memories` no SQLite:

| Campo | Descrição |
|-------|-----------|
| `kind` | `correction`, `preference`, `pattern`, `fact` |
| `scope` | `global`, `skill`, `sku`, `location`, `supplier`... e o identificador do escopo |
| `content` | Texto curto e objetivo |
| `confidence` | Aumenta quando a memória é útil e diminui quando é contrariada |
| `use_count`, `last_used_at` | Uso e recência, para ranking e descarte |
| `source_plan_id` | Plano ou tarefa que originou a memória |

- **Origem:** correções do operador, rejeições na aprovação, erros de validação recorrentes e o resultado das execuções.
- **Busca:** FTS5 com ranking BM25, combinado com filtro por escopo, confiança e recência.
- **Manutenção:** um job periódico deduplica, mescla memórias parecidas e descarta as de baixa confiança ou sem uso.
- **Embeddings** (`sqlite-vec`) são uma evolução opcional, adotada só se a busca textual se mostrar insuficiente.

### Política de RAG

| Usar RAG | Não usar RAG |
|----------|--------------|
| Memórias, skills, receitas | Saldos, endereços, pedidos e qualquer dado estruturado: são lidos por consultas tipadas |

---

## 9. Skills

```
skills/
└── armazenagem/
    ├── SKILL.md     # Instruções padrão: passos, regras do negócio, exemplos curtos de planos
    └── skill.json   # Nome, descrição, gatilhos, consultas e operações permitidas, versão
```

- O prompt de sistema leva só o índice das skills. O conteúdo completo entra apenas quando a skill é ativada.
- O `SKILL.md` traz exemplos de planos válidos, o que reduz tentativas de correção.
- Skills iniciais: `recebimento`, `armazenagem`, `separacao`, `expedicao`, `inventario`, `cadastros`.

---

## 10. Provedores de LLM

- Interface única `ILlmProvider` (chat, tool calling, streaming e contagem de tokens) com adaptadores para **Claude**, **OpenAI** e **DeepSeek**. A API da DeepSeek é compatível com a da OpenAI.
- **Modelo por papel:** um modelo barato para identificar receitas e extrair parâmetros, e um modelo forte para planejar e tratar exceções. Ambos são configuráveis.
- As chaves de API ficam no armazenamento seguro do sistema operacional, nunca no SQLite em texto puro.

---

## 11. Persistência (SQLite)

| Grupo | Conteúdo |
|-------|----------|
| Domínio | Produtos, endereços, saldos, lotes, pedidos de entrada e saída |
| Planos | Plano JSON, status (`rejected`, `pending_approval`, `executed`, `failed`), diff simulado, tentativas |
| Receitas | Plano parametrizado, parâmetros, versões da DSL e da skill, estatísticas de uso e falha |
| Memórias | Tabela `memories` + índice FTS5 |
| Auditoria | Quem pediu, chamadas de ferramenta, plano final, aprovação e resultado |

Toda execução de plano roda numa única transação SQLite.

---

## 12. Segurança e auditoria

- O LLM **nunca** escreve SQL nem acessa o banco diretamente. Todo dado vindo do LLM é tratado como entrada não confiável e validado.
- As operações do catálogo usam consultas parametrizadas.
- Todo plano, aprovado ou rejeitado, fica registrado com sua origem: pedido, skill, modelo e receita.
- Desfazer um plano reversível gera um **plano compensatório**, que passa pelo mesmo fluxo.
