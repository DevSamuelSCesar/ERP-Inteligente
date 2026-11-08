# Arquitetura

> Escopo atual: módulo de **armazém (WMS)**. Stack definida em [`STACK.md`](STACK.md).

## 1. Princípio central

**O LLM descreve as alterações; o código as executa.**

O LLM usa tool calling o tempo todo, mas de duas formas diferentes:

- **Ferramentas de leitura** (`get_stock`, `search_locations`, `search_recipes`...) executam na hora e devolvem dados ao LLM.
- **Alterações de dados** nunca acontecem por chamadas individuais. O LLM monta um **plano JSON** com todos os passos e o envia por uma única ferramenta, `propose_plan`. Um motor determinístico, sem LLM, valida o plano e simula o efeito. Depois que quem pediu confirma, o motor executa o plano numa única transação: aplica tudo ou nada.

Assim, a consistência dos dados é garantida pelo código (validação + transação) e não depende do raciocínio do modelo durante a execução.

---

## 2. Premissas núcleo

| # | Premissa | Como a arquitetura atende |
|---|----------|---------------------------|
| P1 | Pouca intervenção manual do operador | O LLM planeja de ponta a ponta. O humano confirma cada proposta; planos de risco crítico exigem um supervisor diferente de quem pediu. |
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
│  → propose_plan(plano JSON)                                  │
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
5. **Planejamento:** o LLM consulta o estado com ferramentas de leitura e chama `propose_plan`, com título, justificativa, suposições e o plano.
6. **Validação:** se o plano for inválido, o erro volta ao LLM em formato estruturado para ser corrigido, com limite de tentativas. Ao atingir o limite, a tarefa é escalada ao operador.
7. **Simulação:** o dry-run calcula o efeito (o que entra, o que sai e o que muda de endereço) e o nível de risco do plano.
8. **Confirmação e aprovação:** os planos montados pela IA voltam ao chat como **propostas** (validadas e simuladas, nada gravado). Podem ser até 3 alternativas, e o chat oferece até 4 opções clicáveis. Quem pediu escolhe uma e confirma, ou descarta. Ao confirmar, um plano reversível segue direto; um plano de risco crítico vai a `pending_approval` e precisa de um supervisor diferente de quem pediu, que vê o efeito em linguagem de negócio.
9. **Execução:** o Executor aplica todos os passos numa transação. Se qualquer passo falhar, nada é gravado.
10. **Aprendizado:** o resultado é registrado; memórias e receitas são atualizadas (seções 7 e 8).

### Exemplo

```
LLM → search_locations({ zone: "A", min_free: 20 })    ← leitura, executa na hora
    ← { count: 2, rows: [{ code: "A-03-02", free: 20 }, { code: "A-03-04", free: 30 }] }
LLM → propose_plan({ title, rationale, assumptions, plan: { ...plano... } })
    ← { status: "valid", proposals: 1, risk: "reversible" }
Operador confirma → plano 812 executed: "40 cx ABC-123: DOCA-01 → A-03-02 (20), A-03-04 (20)"
```

### Modo proativo

O prompt de sistema (`Core/Planner/Prompts/system.md`) manda o Planner agir como um operador experiente:

- Faz o que o operador poderia fazer pelas operações do catálogo, sem pedir confirmação antes de montar o plano.
- Faltou informação: assume um valor razoável, lista cada suposição em `assumptions` e, havendo caminhos diferentes, propõe até 3 alternativas.
- Entidade inexistente (zona, endereço, produto, fornecedor ou cliente): confere com a busca e propõe um plano que a cria antes da movimentação. Os passos se encadeiam, e a validação enxerga os passos anteriores.
- Pergunta só quando não há padrão razoável, por `offer_options` (1 a 4 opções).
- Recusa só pedido fora do escopo ou sem direito. Diz quem pode fazer e oferece uma alternativa.

Padrões quando não foi dito: endereço novo no próximo código livre da zona, com a capacidade típica da zona (ou 100); recebimento numa doca ativa com espaço; lote com a data de hoje (`AAAAMMDD`) e o SKU; unidade desconhecida `un`; SKU novo com três letras do nome e o primeiro número livre.

---

## 5. Plano JSON (DSL)

```json
{
  "dsl_version": "1.0",
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

**DSL v1 (implementada em `Core/Engine`):**
- **Estrutura:** só os campos `dsl_version` (`"1.0"`), `intent` e `skill` (`^[a-z_]{1,40}$`) e `steps`, com 1 a 50 passos. Cada passo tem só `op`, `args` e o `as` opcional (`^[a-z][a-z0-9_]{0,31}$`, único no plano).
- **Proteção contra entrada maliciosa:**
  - o documento tem no máximo 64 KB e profundidade 16;
  - chaves JSON duplicadas, comentários e vírgulas sobrando são recusados.
- **Referências:** uma referência `"$nome"` só pode apontar para um passo anterior.
- **Argumentos:** cada operação valida os seus por um JSON Schema (`Core/Engine/Schemas/v1/*.schema.json`, draft 2020-12, `additionalProperties: false`). Esses mesmos schemas descrevem as ferramentas do LLM na feature 3.
- **Formatos:**
  - SKU: `^[A-Z]{3}-[0-9]{3}$`;
  - endereço: `^[A-Z]{1,4}-[0-9]{2}(-[0-9]{2})?$`;
  - usa-se `[0-9]` e não `\d`, porque no .NET `\d` aceita dígitos de outros alfabetos.
- **Erros:** são estruturados (`passo`, `campo`, `código`, mensagem em português de negócio). Na pré-visualização eles vão para o operador; na feature 3 voltam para o LLM corrigir.
- **Simulação:** o Simulator executa o plano pelo mesmo código do Executor, dentro de uma transação que sempre termina em `ROLLBACK`. O efeito mostrado na pré-visualização é exatamente o que a execução faria.
- **Execução:** o Executor revalida tudo dentro de `BEGIN IMMEDIATE`, porque o estoque pode ter mudado desde a simulação. Os movimentos, o status do plano e a auditoria são gravados na mesma transação, tudo ou nada.

### Catálogo de operações (armazém, DSL v1)

| Operação | Descrição | Risco | Permissão | Estado |
|----------|-----------|-------|-----------|--------|
| `receive_goods` | Registra a entrada de itens na doca (zona `DOCA`); lote e validade obrigatórios para produto com validade; pode dar baixa num pedido de entrada | Reversível | `stock.receive` | Implementada |
| `move_stock` | Transfere saldo disponível (quantidade − reservado) entre endereços do armazém, respeitando a capacidade | Reversível | `stock.move` | Implementada |
| `adjust_stock` | Ajusta saldo por inventário ou avaria (exige motivo); nunca abaixo do reservado | Crítico | `stock.adjust` | Implementada |
| `create_product` | Cadastra produto; o custo unitário vai cifrado em `unit_cost_enc` | Reversível | `catalog.manage` | Implementada |
| `update_product` | Altera dados do produto; custo cifrado; não liga controle de validade com saldo sem lote | Reversível | `catalog.manage` | Implementada |
| `create_supplier` | Cadastra fornecedor; nome único na tabela e documento opcional, já mascarado | Reversível | `catalog.manage` | Implementada |
| `create_customer` | Cadastra cliente; nome único na tabela e documento opcional, já mascarado | Reversível | `catalog.manage` | Implementada |
| `create_zone` | Cadastra zona do armazém; código único por armazém | Reversível | `location.manage` | Implementada |
| `update_zone` | Renomeia ou (des)ativa zona; zona com saldo não pode ser desativada | Reversível | `location.manage` | Implementada |
| `create_location` | Cadastra endereço numa zona ativa do armazém | Reversível | `location.manage` | Implementada |
| `create_location_range` | Cadastra endereços em lote (`ZONA-CC-NN`, por corredor e níveis), tudo ou nada | Reversível | `location.manage` | Implementada |
| `update_location` | Altera capacidade ou ativa/desativa endereço; capacidade não cai abaixo da ocupação | Reversível | `location.manage` | Implementada |
| `create_inbound_order` | Pedido de entrada de fornecedor ativo; documento único no armazém | Reversível | `order.manage` | Implementada |
| `cancel_inbound_order` | Cancela pedido de entrada do armazém, só enquanto nada foi recebido | Crítico | `order.manage` | Implementada |
| `create_outbound_order` | Pedido de saída de cliente ativo; número único no sistema | Reversível | `order.manage` | Implementada |
| `reserve_stock` | Reserva saldo para o pedido de saída por FEFO, sem passar do que as linhas ainda pedem | Reversível | `stock.reserve` | Implementada |
| `release_reservation` | Devolve ao disponível as reservas do pedido que ainda não foram separadas | Reversível | `stock.reserve` | Implementada |
| `pick_items` | Leva o saldo reservado do pedido a uma doca (movimento `pick`); continua reservado | Reversível | `stock.pick` | Implementada |
| `ship_order` | Expede pedido totalmente separado; baixa o saldo das docas e as reservas | Crítico | `stock.ship` | Implementada |
| `cancel_outbound_order` | Cancela pedido ainda não expedido e libera todas as suas reservas | Crítico | `order.manage` | Implementada |

Planos podem encadear passos de criação (`create_zone`, depois `create_location_range`, depois `move_stock`): a validação enxerga os passos anteriores.

O risco do plano é o **maior risco** entre seus passos.

---

## 6. Ferramentas expostas ao LLM

| Ferramenta | Tipo | Função |
|------------|------|--------|
| Consultas de domínio (`get_stock`, `search_locations`, `search_orders`, `search_partners`...) | Leitura | Respostas compactas e paginadas |
| `search_memories` | Leitura | Busca memórias além das já injetadas no contexto |
| `search_recipes` | Leitura | Busca receitas similares para usar como base |
| `load_skill` | Leitura | Carrega o `SKILL.md` de uma skill |
| `propose_plan` | Escrita (via motor, só simula) | Única porta de entrada para alterar dados; nada é gravado antes da confirmação de quem pediu |
| `remember` | Memória | Registra uma memória candidata (seção 8) |

A skill ativa define quais consultas e quais operações do catálogo ficam disponíveis.

**Implementado na feature 3 (`Core/Planner`), redesenhado em 2026-11-08 para o modo proativo:**
- **Ferramentas de leitura (8):** `search_products`, `get_product`, `get_stock`, `search_locations`, `search_zones`, `search_lots`, `search_orders` e `search_partners`.
  - Cada ferramenta tem JSON Schema próprio (`additionalProperties: false`), validado antes de tocar o banco.
  - Rodam na conexão de leitura da IA (`v_ai_*`), sempre filtradas pelo armazém da sessão.
  - Devolvem no máximo 50 linhas e 8 KB.
  - Exigem `stock.read`.
- **Propostas:** `propose_plan` recebe título, justificativa, suposições e o plano DSL v1.
  - Só existe se o direito efetivo tiver `plan.submit`; as operações oferecidas no prompt e no schema são as do direito IA ∩ usuário.
  - Um plano inválido volta como `[{step, field, code, message}]` e conta como tentativa.
  - Um plano válido é simulado e vira **proposta**. Podem ser até **3 alternativas** válidas, cada uma com título, justificativa e suposições.
  - Nada é gravado: a confirmação de quem pediu executa a proposta escolhida.
- **Opções:** `offer_options` mostra de 1 a 4 botões (rótulo até 60 caracteres, pedido completo até 300). Ao clicar, o pedido é enviado como se tivesse sido digitado.
- **Pesquisa robusta:** toda entidade que a IA precisa encontrar tem ferramenta de pesquisa, não só busca por chave exata.
  - O texto livre aceita parte do nome, da descrição, do código ou do SKU; ignora maiúsculas e acentos e exige todos os termos.
  - Filtros por entidade: saldo no armazém, validade, unidade, zona, corredor, espaço livre, ativo ou inativo, vencimento e tipo de parceiro.
  - Paginação com `limit` (máximo 50) e `offset`, com `total` e `has_more`.
  - O texto do usuário vira só parâmetro: `%` e `_` nunca viram curinga.
  - `search_locations` com `zone` devolve também `next_free_codes` (próximos 5 códigos livres, únicos no sistema) e `typical_capacity`.
  - O custo unitário nunca é lido.
- **Delimitação:** o resultado de cada ferramenta volta ao LLM entre `<dados fonte="tool:…">…</dados>`, com `<` e `>` escapados, e o prompt de sistema manda tratá-lo como dado.
- **Auditoria:** cada chamada é auditada como `ai.tool` (permitida ou negada), em nome do usuário.
- **Limites por pedido:**
  - 4 planos inválidos;
  - 12 rodadas com o LLM e até 8 chamadas de ferramenta por rodada;
  - 3 propostas válidas;
  - 8.192 tokens por chamada e 60.000 por pedido;
  - ao estourar um limite, a tarefa é **escalada** ao operador, exceto quando já há propostas válidas: essas são entregues.
- **Histórico:** os últimos 10 pedidos e respostas da sessão ficam só em memória, nunca no banco.

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
- **Proteção:** memórias passam por um filtro antes de serem gravadas: tamanho máximo de 1.000 caracteres, sem dados sensíveis (CPF, e-mail, telefone, CNPJ completo) e sem texto que pareça instrução ao modelo. Memórias criadas a partir de conteúdo externo entram com confiança baixa.

### RAG

| Etapa | Escolha |
|-------|---------|
| Fontes | Procedimentos e políticas do armazém (`knowledge/*.md`), `SKILL.md` das skills e descrições de receitas |
| Chunking | Um trecho por seção `##` do Markdown, com o título do documento e da seção no início do trecho; seções acima de ~500 tokens são divididas com sobreposição de ~50 tokens |
| Embeddings | `text-embedding-3-small` (OpenAI, 1.536 dimensões). Anthropic e DeepSeek não oferecem API de embeddings. Alternativa local, sem enviar texto para fora: um modelo de embeddings aberto rodando na própria máquina |
| Banco vetorial | `sqlite-vec` (tabela virtual `vec0`) no mesmo arquivo SQLite, ligada a `knowledge_chunks` |
| Busca | Híbrida: top-20 do FTS5/BM25 + top-20 por similaridade de cosseno, combinados por *Reciprocal Rank Fusion*; os 4 melhores trechos entram no contexto |
| Atualização | Cada trecho guarda o SHA-256 do conteúdo; só trechos alterados geram embedding de novo |

Os trechos recuperados entram no prompt **delimitados como dados de referência**, com o caminho do documento de origem, e nunca como instruções (seção 12.8).

### Política de RAG

| Usar RAG | Não usar RAG |
|----------|--------------|
| Procedimentos, políticas, skills, memórias, receitas | Saldos, endereços, pedidos e qualquer dado estruturado: são lidos por consultas tipadas |

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

- Interface única `ILlmProvider` (chat com tool calling) com adaptadores para **Claude**, **OpenAI** e **DeepSeek**. A API da DeepSeek é compatível com a da OpenAI.
- **Estado (feature 3):** só a DeepSeek está implementada (`Core/Llm/DeepSeekProvider`), sem streaming, porque a UI mostra a resposta completa.
  - **Endereço:** fixo no código (`https://api.deepseek.com`), só HTTPS e sem seguir redirecionamentos, para a chave não poder ir a outro host.
  - **Tempo e tentativas:** 60 s por tentativa e até 2 novas tentativas, só para 429 e 5xx.
  - **Raciocínio:** no modo de raciocínio da DeepSeek, o `reasoning_content` de cada resposta é devolvido nas chamadas seguintes, como a API exige quando há ferramentas.
  - **Modelos:** `deepseek-v4-pro` (padrão) e `deepseek-flash`, escolhidos em Configurações.
- **Modelo por papel:** um modelo barato para identificar receitas e extrair parâmetros, e um modelo forte para planejar e tratar exceções. Ambos são configuráveis.

| Papel | Modelo padrão | Alternativas suportadas |
|-------|---------------|-------------------------|
| Planner | Claude Sonnet 5.5 (`claude-sonnet-5-5`) | Claude Opus 5.5, `deepseek-v4-pro`, modelos da OpenAI |
| Receitas e extração de parâmetros | Claude Haiku 5.5 (`claude-haiku-5-5`) ou o modelo ajustado por fine-tuning (seção 10.1) | `deepseek-flash` |
| Embeddings | `text-embedding-3-small` | Modelo de embeddings local |

A justificativa e o custo por 1.000 usos estão em [`IA.md`](IA.md).

- As chaves de API ficam no armazenamento seguro do sistema operacional, nunca no SQLite nem no repositório.
  - A chave da DeepSeek é digitada no app, em Configurações, só por quem tem `user.manage` (administrador). O campo é mascarado e a chave nunca é reexibida.
  - Fica no cofre na entrada `llm.deepseek.key` do banco. Salvar, remover e trocar o modelo geram auditoria sem o valor.

### 10.1 Fine-tuning

Experimento para reduzir custo e dependência de rede na etapa mais frequente: transformar um pedido em uma receita com parâmetros.

| Item | Definição |
|------|-----------|
| Tarefa | Pedido em linguagem natural → JSON `{intent, recipe, params}` (ou `{"intent":"none"}` quando nenhuma receita serve) |
| Dados | ~500 pedidos fictícios gerados a partir das receitas e do catálogo de teste, com variações de escrita, erros de digitação e pedidos fora do escopo; anotados e revisados pelo grupo; divisão 80/20 |
| Modelo base | Modelo aberto pequeno (classe 1–3 bilhões de parâmetros, ex.: Qwen2.5-1.5B-Instruct) com LoRA, rodando localmente |
| Métricas | *Exact match* do JSON, F1 por campo de parâmetro, taxa de JSON válido e taxa de acerto em "nenhuma receita" |
| Comparação | Mesmo conjunto de teste com Claude Haiku 5.5 em *few-shot*; o modelo ajustado só substitui o Haiku se empatar ou superar no *exact match* |

---

## 11. Persistência (SQLite)

| Grupo | Conteúdo |
|-------|----------|
| Domínio | Produtos, endereços, saldos, lotes, fornecedores, clientes, pedidos de entrada e saída |
| Livro-razão | `stock_movements`: uma linha por mudança de estoque, nunca alterada nem apagada |
| Reservas | `order_reservations`: uma linha por parte da reserva de um pedido de saída, com estado `reserved`, `picked`, `shipped` ou `released`; quantidade imutável e sem `DELETE` |
| Planos | Plano JSON, status (`validating`, `pending_approval`, `rejected`, `executed`, `failed`), diff simulado, tentativas, aprovador, quem rejeitou e o motivo (`rejected_by`, `rejection_reason`), causa da falha (`failure_reason`) |
| Receitas | Plano parametrizado, parâmetros, versões da DSL e da skill, estatísticas de uso e falha |
| Memórias | Tabela `memories` + índice FTS5 |
| Conhecimento (RAG) | `knowledge_chunks` + índice FTS5 + vetores `sqlite-vec` |
| Acesso | `roles`, `role_permissions` (ALLOW/DENY), `users` |
| Auditoria | `audit_log` encadeado por hash |

O schema está em `db/schema.sql` e as regras de banco em `db/security.sql`. Toda execução de plano roda numa única transação SQLite. A versão atual é a 4 (`PRAGMA user_version`); bancos antigos sobem pelas migrações de `db/migrations/` (002, 003 e 004).

---

## 12. Segurança de dados

### 12.1 Princípios

- **Defesa em profundidade:** cada regra importante é aplicada em pelo menos duas camadas (aplicação e banco).
- **Menor privilégio:** cada componente recebe só o acesso de que precisa. A IA tem o menor acesso de todos.
- **A IA é um usuário não confiável:** tudo o que vem do LLM (planos, argumentos, memórias) é tratado como entrada externa e validado como se viesse de um formulário.
- **Nada some:** movimentos, planos e auditoria são append-only. Correções são feitas por novos registros.

### 12.2 Classificação dos dados

| Classe | Exemplos | Regra |
|--------|----------|-------|
| Público | SKU, nome e descrição de produto, código de endereço, glossário | Pode aparecer em qualquer tela e no contexto da IA |
| Interno | Saldos, lotes, pedidos, nome de fornecedor e cliente, planos, memórias | Só usuários autenticados com permissão; a IA vê pelas views `v_ai_*` |
| Sensível | CPF, nome completo, e-mail e telefone de pessoas, CNPJ/CPF completo de terceiros, endereço de entrega, custo unitário, hash de senha | Criptografado por coluna, exibido mascarado, nunca enviado ao LLM, leitura só com `sensitive.read` |

A classificação coluna por coluna está em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md).

### 12.3 Proteção em repouso

1. **Banco inteiro criptografado com SQLCipher** (AES-256). A chave é aleatória, gerada na instalação e guardada no cofre do sistema operacional: DPAPI no Windows, Keychain no macOS, Secret Service (libsecret) no Linux. Sem a chave, o arquivo `.db` copiado é ilegível.
   - São duas chaves aleatórias de 32 bytes por banco: a do SQLCipher e a das colunas. No cofre, cada uma é identificada pelo caminho absoluto do arquivo `.db` e pelo nome (`sqlcipher` ou `column`). No Linux usa o schema libsecret `app.nexus.erp`, com os atributos `db` e `key`.
   - O SQLCipher abre com a chave bruta (`PRAGMA key = "x'<hex>'"`), sem derivação por senha.
   - Se o arquivo `.db` existe e a chave não está no cofre, o app para com erro e **nunca** gera uma chave nova, porque isso tornaria os dados irrecuperáveis.
   - Estado atual: Linux (libsecret) e Windows (DPAPI) implementados; o macOS (Keychain) ainda falha com erro explícito.
2. **Colunas sensíveis (`*_enc`) com AES-256-GCM** na aplicação, com uma segunda chave, também no cofre do sistema. Cada valor guarda nonce e tag de autenticação; um valor adulterado falha na leitura.
   - Formato do valor: `0x01` (versão) | nonce de 12 bytes | texto cifrado | tag de 16 bytes.
   - Dados associados (AAD): `"{tabela}.{coluna}#{id}"`. Um valor copiado para outra linha ou coluna também falha na leitura.
3. **Mascaramento:** colunas `*_masked` e as views `v_*_masked` mostram só parte do dado (ex.: `**.***.***/0001-**`) para quem não tem `sensitive.read`.
4. **Senhas** guardadas apenas como hash Argon2id, no formato PHC `$argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>` (64 MiB, 3 iterações, paralelismo 4, conforme a RFC 9106; salt de 16 bytes e hash de 32 bytes).
   - A verificação usa os parâmetros gravados na própria string, compara em tempo constante e recusa parâmetros fora de limites seguros (proteção contra uma linha adulterada que force um custo absurdo).
5. **Backups** feitos pela API de backup do SQLite a partir do banco aberto com SQLCipher, então continuam criptografados.

### 12.4 Proteção em trânsito e minimização

- Chamadas aos provedores de LLM e de embeddings só por HTTPS (TLS 1.2 ou superior).
- O contexto enviado ao LLM é montado **somente** com dados das views `v_ai_*`, trechos do RAG e memórias filtradas. Dados sensíveis nunca saem da máquina.
- Logs técnicos passam por um filtro que remove padrões de CPF, CNPJ, e-mail, telefone e chaves de API.

### 12.5 Controle de acesso

- **Autenticação local** com usuário e senha (Argon2id), bloqueio temporário após tentativas falhas e sessão que expira por inatividade:
  - **Bloqueio:** 5 falhas seguidas bloqueiam o usuário por 15 minutos (`users.failed_attempts`, `users.locked_until`). Um login correto zera o contador.
  - **Mensagem única:** a resposta é sempre "Usuário ou senha inválidos", exista o usuário ou não. Usuário inexistente também passa por uma verificação Argon2 falsa, para que o tempo de resposta não revele quem existe.
  - **Contas que não fazem login:** usuários inativos e a conta de serviço da IA (`svc.ia`).
  - **Sessão:** expira após 15 minutos sem interação de teclado ou mouse e volta à tela de login.
  - **Auditoria:** login, falha, bloqueio, saída e expiração geram eventos em `audit_log`.
- **Papéis e permissões** em `role_permissions`, com efeito `ALLOW` ou `DENY`. A regra é *deny-overrides*: um `DENY` sempre vence. A view `v_effective_permissions` calcula o resultado.
- **Escopo por linha (equivalente a RLS):** toda consulta da aplicação recebe o `warehouse_id` do usuário como parâmetro; um usuário nunca lê nem altera dados de outro armazém.
- **A IA age em nome de alguém:** o direito efetivo da IA é a **interseção** entre o papel `agente_ia` e o papel de quem fez o pedido. Ela nunca tem mais permissão do que a pessoa.
  - O motor aplica a interseção a todo plano com `planned_by = 'ai'`: na prévia, no envio e de novo na aprovação.
  - Desde 2026-11-08 (decisão do product owner, migração 003), a IA tem os mesmos direitos operacionais do usuário: recebimento, movimentação, reserva, separação, expedição, ajuste, cadastros e pedidos.
  - Riscos aceitos e mitigados: toda proposta exige **Confirmar** de quem pediu; planos críticos exigem outro supervisor; a interseção impede que a IA faça o que a pessoa não pode. Detalhes em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), seção 3.1.
  - Continuam proibidos para a IA: aprovar ou rejeitar plano, ler dados sensíveis, gerir usuários e papéis, ler a auditoria e promover receitas (a escalonamento é bloqueado por trigger e os `DENY` não podem ser removidos).
- **Segregação de funções:** quem pede uma operação crítica não pode aprová-la, e a IA nunca aprova.
- A matriz completa de papéis, com os pontos de `DENY`, está em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md).

### 12.6 Conexões ao banco

O SQLite não tem usuários de banco, então o isolamento é feito por conexões com poderes diferentes:

| Conexão | Usada por | Modo | Restrições |
|---------|-----------|------|------------|
| Leitura da IA | Ferramentas de leitura do Planner | `Mode=ReadOnly` | *Authorizer callback* (`sqlite3_set_authorizer`): só lê tabelas através das views `v_ai_*`, nega colunas sensíveis e qualquer escrita, DDL, `ATTACH` ou `PRAGMA` |
| Executor | Motor determinístico | Leitura e escrita | Só executa operações do catálogo da DSL, com consultas parametrizadas, dentro de transação |
| Aplicação | Telas e policy engine | Leitura e escrita | Todas as consultas passam pelo policy engine (permissão + escopo por armazém) |

O comportamento de referência do *authorizer* está implementado e testado em `db/tests/test_db.py` e foi portado para C# em `Core/Data`. A versão C# também nega a função `load_extension`.

Regras comuns às três conexões:
- São abertas sem pool. A chave do SQLCipher é aplicada logo após a abertura e validada com uma leitura de `sqlite_master` antes de qualquer uso.
- As conexões de escrita ligam `foreign_keys`.
- A da IA instala o *authorizer* depois da chave. Como ele nega `BEGIN`, a conexão da IA nunca abre transação.

No policy engine (`Core/Policy`):
- Consultas por armazém precisam conter o parâmetro `$warehouse_id`, que o próprio engine preenche com o armazém da sessão. Uma consulta sem esse parâmetro é recusada antes de chegar ao banco.
- Consultas de catálogo sem armazém usam uma chamada separada e explícita.
- Toda permissão negada lança exceção e gera um evento `denied` na auditoria.

### 12.7 Integridade e auditoria

- **Triggers de banco** (`db/security.sql`) bloqueiam com `RAISE(ABORT, 'DENY: ...')`:
  - UPDATE e DELETE em `audit_log` e `stock_movements`;
  - alteração de planos finalizados e exclusão de qualquer plano;
  - concessão de `plan.approve`, `user.manage`, `role.manage`, `sensitive.read` e `audit.read` ao papel da IA, e remoção dos seus `DENY`;
  - `order_reservations` sem `DELETE`, e `UPDATE` só nas transições `reserved → picked → shipped` e `reserved`/`picked → released`, com quantidade e identidade imutáveis;
  - aprovação ou rejeição de plano pela IA ou pelo próprio solicitante, e plano ao mesmo tempo aprovado e rejeitado.
- **Auditoria encadeada:** cada linha de `audit_log` guarda o hash SHA-256 da linha anterior. Um job verifica a cadeia ao iniciar o app e periodicamente; qualquer quebra gera alerta ao administrador.
  - **Regra v1 do hash:** `hash` = SHA-256 em hexadecimal minúsculo dos bytes UTF-8 de
    `"nexus-audit-v1\n" + prev_hash + "\n" + F(id) F(occurred_at) F(actor_type) F(actor_id) F(on_behalf_of) F(action) F(target) F(outcome) F(details_json)`,
    em que `F(nulo) = "-;"` e `F(v) = <tamanho de v em bytes UTF-8> + ":" + v + ";"` (inteiros em decimal).
  - A primeira linha tem `prev_hash = "GENESIS"`, e cada linha seguinte guarda o `hash` da anterior (ordem por `id`).
  - O escritor insere com `BEGIN IMMEDIATE`, com `id` e `occurred_at` (`AAAA-MM-DDTHH:MM:SSZ`, UTC) explícitos, para que o hash seja calculado sobre os mesmos valores gravados.
  - Há implementações de referência iguais em Python (`db/tests/test_db.py`) e C# (`Core/Audit`); as linhas do seed já têm os hashes reais.
  - O alerta de quebra aparece no app para quem tem `audit.read`, indicando a primeira linha inválida.
- **O que é auditado:** login, cada chamada de ferramenta da IA (permitida ou negada), cada plano (submetido, rejeitado, aprovado, executado), mudanças de permissão e leituras de dados sensíveis.

### 12.8 Defesa contra prompt injection

Camadas, da mais externa para a mais interna:

1. **Conteúdo é dado, não instrução:** descrições de produtos, documentos do RAG, memórias e resultados de ferramentas entram no prompt dentro de blocos delimitados e marcados como dados de referência. O prompt de sistema diz explicitamente que instruções dentro desses blocos devem ser ignoradas e relatadas.
2. **Ferramentas limitadas pela skill:** a IA só vê as ferramentas da skill ativa.
3. **Sem SQL e sem escrita direta:** a única forma de alterar dados é `propose_plan`, com operações de um catálogo fechado, executadas só depois da confirmação de quem pediu.
4. **Validação determinística:** schema, regras de negócio e permissões (interseção IA ∩ usuário) são checados pelo código.
5. **Aprovação humana** para todo plano de risco crítico, com o diff simulado em linguagem de negócio.
6. **Banco:** mesmo que tudo acima falhe, a conexão da IA é somente leitura e os triggers bloqueiam as escritas proibidas.

### 12.9 Defesa contra SQL injection

- Toda consulta usa **parâmetros** (`$param` / `?`); concatenação de texto em SQL é proibida e verificada em revisão de código.
- Identificadores dinâmicos (ordenação, filtros) vêm de listas fixas no código, nunca do usuário ou do LLM.
- Argumentos de ferramentas são validados por JSON Schema (tipos, tamanhos, padrões como `^[A-Z]{3}-\d{3}$` para SKU) antes de chegar ao banco.
- A conexão da IA nega statements de escrita mesmo que um texto malicioso chegue a ela.

### 12.10 Segredos

- Chaves de API, chave do SQLCipher e chave de colunas ficam no cofre do sistema operacional.
- Nenhum segredo vai para o repositório: `.gitignore` bloqueia `.env*`, `*.key`, `*.pem` e arquivos `*.db`.
- Os dados do repositório são todos fictícios.

### 12.11 Testes de segurança

- `db/tests/test_db.py`: imutabilidade, escalonamento de privilégio, interseção IA ∩ usuário, *authorizer* da IA, SQL injection, otimização, migrações 2 → 3 → 4 e reservas (`MigrationTo3Tests`, `MigrationTo4Tests`, `ReservationTests`).
- Testes do app em `tests/Nexus.Tests`: catálogo (`CatalogOperationTests`, `OrderOperationTests`), pesquisa (`SearchToolsTests`), propostas da IA (`ProposePlanTests`) e migrações (`MigrationTests`).
- Suíte de ataques de prompt injection e de SQL injection descrita em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), com métricas.
