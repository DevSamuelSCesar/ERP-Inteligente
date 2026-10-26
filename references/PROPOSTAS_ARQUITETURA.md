# Propostas de Arquitetura

> Status: **decidido**. A arquitetura adotada está em [`ARQUITETURA.md`](ARQUITETURA.md). Este documento é mantido como histórico das alternativas avaliadas.
> Escopo: apenas o módulo de **armazém (WMS)**. Stack definida em [`STACK.md`](STACK.md).

---

## 1. Premissas núcleo

Todas as propostas precisam atender a estas premissas:

| # | Premissa | O que implica |
|---|----------|---------------|
| P1 | Pouca intervenção manual do operador | O LLM executa as tarefas de ponta a ponta. O humano só confirma ações críticas ou ambíguas. |
| P2 | Pouca necessidade de conhecimento técnico | O operador conversa em linguagem natural. Confirmações e erros aparecem em termos do negócio, nunca em termos técnicos. |
| P3 | Automação confiável via tool calling | Toda ação passa por ferramentas tipadas e validadas. O LLM nunca escreve SQL nem altera dados diretamente. |
| P4 | Sistema de skills do ERP | Os procedimentos do armazém ficam em skills versionadas (Markdown + JSON), carregadas sob demanda. |
| P5 | Eficiência de tokens | Instruções padrão curtas, contexto mínimo por tarefa, ferramentas expostas conforme a skill ativa e respostas de ferramenta compactas. |
| P6 | RAG quando houver vantagem | Usado só para texto livre que cresce (memórias, skills, procedimentos). Dados estruturados são lidos por consultas tipadas. |
| P7 | Melhoria contínua | O modelo grava memórias (correções, preferências, padrões) em SQLite e as consulta rapidamente nas próximas tarefas. |

---

## 2. Domínio do armazém (comum às três propostas)

| Conceito | Descrição |
|----------|-----------|
| Produto / SKU | Item armazenável, com unidade de medida, dimensões e controle de lote/validade opcional. |
| Endereço | Posição física hierárquica: armazém → zona → rua → prateleira → posição. |
| Saldo | Quantidade de um SKU (e lote) em um endereço. |
| Lote / validade | Rastreabilidade e regras como FEFO (o que vence primeiro sai primeiro). |
| Recebimento | Entrada de mercadoria, conferência contra o pedido de compra ou nota e registro de divergências. |
| Armazenagem (putaway) | Escolha do endereço de destino e movimentação da doca até a posição. |
| Separação (picking) | Coleta dos itens de um pedido de saída, com roteiro de endereços. |
| Expedição | Conferência final, embalagem e baixa do estoque. |
| Inventário | Contagem cíclica ou geral, com ajuste das divergências. |
| Movimentação / ajuste | Transferência entre endereços e correções de saldo, sempre com motivo. |

---

## 3. Blocos comuns às três propostas

### 3.1 Provedores de LLM
- Interface única `ILlmProvider` (chat, tool calling, streaming e contagem de tokens) com adaptadores para **Claude**, **OpenAI** e **DeepSeek**. A API da DeepSeek é compatível com a da OpenAI, então pode reaproveitar o mesmo adaptador com outra URL base.
- Configuração de **modelo por tipo de tarefa**: modelo barato para classificação e extração, modelo forte para planejamento e exceções.
- As chaves de API ficam no armazenamento seguro do sistema operacional, nunca no SQLite em texto puro.

### 3.2 Skills
Cada skill é uma pasta:

```
skills/
└── recebimento/
    ├── SKILL.md     # Instruções padrão: passos, regras do negócio, exemplos curtos
    └── skill.json   # Nome, descrição, gatilhos, ferramentas permitidas, nível de risco, versão
```

- **Carregamento progressivo:** o prompt de sistema leva só um índice das skills (nome + descrição de uma linha). O `SKILL.md` completo entra no contexto apenas quando a skill é ativada.
- O `skill.json` define **quais ferramentas** ficam visíveis ao LLM enquanto a skill está ativa. Isso reduz tokens e erros de escolha de ferramenta.

### 3.3 Memória (SQLite)
Tabela `memories`: `id`, `kind` (correção, preferência, padrão, fato), `scope` (global, skill, SKU, endereço, fornecedor...), `content`, `confidence`, `use_count`, `created_at`, `last_used_at`, `source` (tarefa de origem).

- Busca textual por **FTS5 com ranking BM25**: rápida, local e sem custo de embeddings.
- Etapa opcional: embeddings com `sqlite-vec` para busca semântica, adotada só quando o volume e a qualidade da busca textual justificarem.
- Recuperação **limitada** por tarefa (top-k pequeno, filtrado por escopo) para não inflar o contexto.

### 3.4 Política de RAG
| Usar RAG | Não usar RAG |
|----------|--------------|
| Memórias, skills, procedimentos operacionais, histórico de correções | Saldos, endereços, pedidos e qualquer dado estruturado: são lidos por ferramentas de consulta tipadas e paginadas |

### 3.5 Segurança das ações
Cada ferramenta declara um **nível de risco**:
- **Leitura:** executa sem confirmação.
- **Escrita reversível:** executa e fica registrada, com opção de desfazer.
- **Escrita crítica** (ajuste de inventário, exclusão, baixa em massa): pede confirmação do operador na UI do Avalonia, com um resumo em linguagem de negócio.

Todas as chamadas de ferramenta vão para um log de auditoria (quem pediu, o que o LLM decidiu, o que foi executado).

---

## 4. Proposta A: Monólito modular com ferramentas geradas do domínio

### Visão geral
Uma aplicação única em camadas, com um único agente. As ferramentas são geradas automaticamente dos comandos e consultas do domínio.

```
UI (Avalonia, MVVM) ── Chat + telas de consulta e confirmação
        │
Agent ── loop de tool calling, skill ativa, memórias
        │
Tools ── geradas dos comandos e consultas (JSON Schema automático)
        │
Application ── comandos e consultas tipados (ex.: ReceiveGoods, MoveStock)
        │
Domain ── entidades e regras do armazém
        │
Infrastructure ── SQLite, provedores de LLM, sistema de arquivos
```

### Como funciona
1. O operador pede: "Recebi 40 caixas do fornecedor X".
2. O agente escolhe a skill `recebimento` pelo índice e o `SKILL.md` é carregado.
3. Só as ferramentas permitidas pela skill ficam visíveis. O LLM as chama em sequência.
4. Cada comando valida as regras de negócio no domínio antes de persistir.
5. No fim, uma etapa curta de reflexão decide se algo deve virar memória (ferramenta `remember`).

### Destaques
- **Records C# com atributos** descrevem os comandos. Um source generator produz o JSON Schema das ferramentas, o que mantém o domínio e as ferramentas sempre sincronizados.
- O mesmo comando atende a UI manual e o LLM, sem lógica duplicada.

### Prós
- Mais simples de construir, testar e depurar.
- Caminho mais curto até o MVP.
- Fácil de evoluir para B ou C depois.

### Contras
- Um agente único concentra o contexto, e o custo por tarefa cresce com o número de módulos.
- O LLM executa as ações passo a passo: um erro no meio da sequência exige compensação manual ou pelo agente.
- O aprendizado se limita a memórias textuais.

---

## 5. Proposta B: Livro-razão de eventos com agentes especializados

### Visão geral
Toda mudança no armazém é um **evento imutável** (append-only) gravado no SQLite. Vários agentes especializados atuam por função, coordenados por um roteador.

```
UI (Avalonia)
    │
Router ── modelo barato classifica a intenção
    │
    ├── Agente de Recebimento  ── ferramentas e skills próprias
    ├── Agente de Separação    ── ferramentas e skills próprias
    ├── Agente de Inventário   ── ferramentas e skills próprias
    └── Agente Supervisor      ── exceções, divergências, escalonamento ao humano
    │
Event Store (SQLite, append-only) ──► Projeções (saldos, ocupação, pendências)
    │
Memory Consolidator (job periódico)
```

### Como funciona
1. O roteador classifica o pedido e o envia ao agente da função.
2. O agente trabalha com poucas ferramentas e só as skills da sua função, então o contexto fica pequeno.
3. Cada ação gera eventos (`GoodsReceived`, `StockMoved`, `CountAdjusted`...). Saldos e relatórios são **projeções** desses eventos.
4. **Desfazer** é um evento compensatório: nada é apagado.
5. Um **consolidador de memórias** analisa periodicamente os eventos e as correções humanas: cria memórias novas, deduplica, ajusta a confiança e descarta as obsoletas.

### Destaques
- Auditoria completa sem esforço extra: o log *é* a fonte da verdade.
- Cada agente pode usar um modelo diferente (DeepSeek no roteador, Claude no supervisor, por exemplo).
- O histórico de eventos é uma base rica para o aprendizado (ex.: detectar endereços com divergência recorrente).

### Prós
- Máxima rastreabilidade e reversibilidade.
- Contexto pequeno por agente, boa eficiência de tokens.
- Escala bem quando novos módulos do ERP forem adicionados (financeiro, compras...).

### Contras
- Maior complexidade: event sourcing, projeções e coordenação entre agentes.
- O roteamento pode errar, então é preciso um fallback para o supervisor.
- Mais código de infraestrutura antes de entregar valor.

---

## 6. Proposta C: O LLM planeja, um motor determinístico executa

### Visão geral
O LLM **não executa** ações de escrita diretamente. Ele gera um **plano em JSON** numa DSL de passos. O plano é validado, simulado, mostrado ao operador quando necessário e executado por um motor determinístico numa única transação.

```
UI (Avalonia) ── pedido + pré-visualização do plano
    │
Planner (LLM) ── consulta dados (ferramentas de leitura) + receitas similares (RAG)
    │  gera
Plano JSON ──► Validador (JSON Schema + regras de negócio)
    │
Dry-run ── simula e calcula o diff de estoque
    │
Executor determinístico ── transação SQLite (tudo ou nada)
    │
Recipe Store ── planos bem-sucedidos viram receitas parametrizadas
```

### Exemplo de plano
```json
{
  "intent": "putaway",
  "steps": [
    { "op": "find_location", "args": { "sku": "ABC-123", "qty": 40, "strategy": "nearest_free" }, "as": "loc" },
    { "op": "move_stock", "args": { "sku": "ABC-123", "qty": 40, "from": "DOCA-01", "to": "$loc" } }
  ]
}
```

### Como funciona
1. O planejador usa ferramentas **somente de leitura** para entender o estado e busca receitas parecidas.
2. Ele gera o plano. O validador rejeita planos inválidos e devolve o erro ao LLM para correção (com limite de tentativas).
3. O dry-run mostra o efeito previsto. Planos de baixo risco executam direto; os críticos pedem confirmação.
4. O executor aplica tudo numa transação: se um passo falhar, nada é aplicado.
5. Planos que funcionam e se repetem viram **receitas** parametrizadas. Na próxima vez, a receita roda **sem LLM** ou só com o LLM preenchendo os parâmetros.

### Destaques
- **Custo de tokens decrescente:** quanto mais o sistema é usado, mais tarefas viram receitas determinísticas.
- O aprendizado é concreto: além de memórias textuais, o sistema acumula **procedimentos executáveis**.
- Atomicidade garantida, sem estados intermediários inconsistentes.

### Prós
- Maior confiabilidade entre as três propostas.
- Melhor eficiência de tokens no médio prazo.
- Pré-visualização clara para o operador (P2).

### Contras
- A DSL limita o que o LLM consegue expressar; tarefas fora dela exigem evoluir a DSL.
- Maior esforço inicial (validador, simulador, motor, armazenamento de receitas).
- Receitas precisam de versionamento e invalidação quando as regras mudam.

---

## 7. Comparativo

| Critério | A: Monólito + agente único | B: Eventos + multiagentes | C: Planejador + motor |
|----------|:--------------------------:|:-------------------------:|:---------------------:|
| Confiabilidade | Média | Alta | Muito alta |
| Eficiência de tokens | Média | Alta | Muito alta (cresce com o uso) |
| Complexidade | Baixa | Alta | Média-alta |
| Auditabilidade | Média (log de chamadas) | Muito alta (event store) | Alta (planos + log) |
| Aprendizado | Memórias textuais | Memórias consolidadas dos eventos | Memórias + receitas executáveis |
| Esforço até o MVP | Baixo | Alto | Médio |
| Escala para outros módulos | Média | Alta | Alta |

---

## 8. Observações para a decisão

- **As propostas se combinam.** Exemplos:
  - **A → C:** começar com A e introduzir planos e receitas quando os fluxos estabilizarem.
  - **B + C:** event store como fonte da verdade, com planos validados e receitas no lugar de agentes que executam passo a passo.
- **Perguntas em aberto para o usuário:**
  1. Qual provedor ou modelo deve ser o padrão, e há restrição de custo mensal?
  2. O sistema precisa funcionar **offline** (sem LLM) para operações básicas?
  3. Haverá **múltiplos usuários ou estações** acessando o mesmo armazém, ou um único posto?
  4. Existem integrações previstas (nota fiscal, leitor de código de barras, outros ERPs)?
  5. Qual o nível de autonomia desejado: o que pode ser executado sem confirmação humana?
