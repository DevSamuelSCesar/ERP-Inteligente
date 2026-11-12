# Pendências

O que ficou pendente depois das features 1 a 10 do `ROADMAP.md` (estado em 2026-11-12). O código e os testes estão prontos. Os itens 1 a 3 dependem só de uma chave de API do usuário. Os itens 4 a 7 são melhorias opcionais.

As chaves são passadas por variável de ambiente só no comando, ficam na memória do processo e nunca são gravadas nem enviadas ao GitHub. Os comandos rodam a partir da raiz do repositório. Cada execução chama uma API paga.

## Dependem de uma chave de API

### 1. Linha de base do Matcher com o Haiku e decisão de adoção (feature 8)

O modelo local (Qwen2.5-1.5B com LoRA) já foi treinado e medido. Falta compará-lo com o Claude Haiku 5.5 *few-shot*, que é a regra de adoção do `references/IA.md`, seção 4.

| Medição | Modelo local, resposta crua | Modelo local, com a checagem do app | Haiku 5.5 |
|---------|----------------------------:|------------------------------------:|----------:|
| *Exact match*, 40 pedidos escritos à mão | 80,0% | 92,5% | pendente |
| Acerto em "nenhuma receita" | 30% | 80% | pendente |
| *Exact match*, teste gerado (100) | 100% | 100% | pendente |

**Precisa de:** `ANTHROPIC_API_KEY`. São cerca de 140 chamadas curtas.

Antes de rodar, gere os conjuntos, que ficam fora do git:

```bash
python tools/finetune/generate_dataset.py
```

Rode a linha de base nos dois conjuntos:

```bash
ANTHROPIC_API_KEY=... python tools/finetune/predict_claude.py --data tools/finetune/data --split holdout --out tools/finetune/data/pred_haiku_holdout.jsonl
ANTHROPIC_API_KEY=... python tools/finetune/predict_claude.py --data tools/finetune/data --out tools/finetune/data/pred_haiku.jsonl
```

Compare com o modelo local:

```bash
python tools/finetune/evaluate.py --gold tools/finetune/data/holdout.jsonl --pred tools/finetune/data/pred_local_holdout.jsonl --baseline tools/finetune/data/pred_haiku_holdout.jsonl --guard
```

As previsões do modelo local (`pred_local*.jsonl`) só existem na máquina onde ele foi servido. Em outra máquina, sirva o modelo (`serve_local.py`) e rode `predict_local.py`, conforme o `tools/finetune/README.md`.

**Depois:**
- Registre os números no `IA.md`, seção 4, no `ROADMAP.md` (feature 8), no `README.md` e no `tools/finetune/README.md`.
- Se `adopt_candidate` der `true`, o administrador pode escolher **Modelo local** para "Receitas (Matcher)" em Configurações.
- Até lá, a recomendação é manter o Matcher no Haiku ou no `deepseek-flash`.

### 2. Conjuntos de avaliação que chamam o LLM (feature 10)

O RAG sem LLM já foi medido. Os relatórios estão em `tools/eval/results`. Faltam quatro conjuntos:
- planejamento: 50 pedidos com gabarito;
- respostas com fonte: 40 perguntas;
- fora do escopo: 30 pedidos;
- proativos ou sem direito: 10 pedidos.

**Precisa de:** a chave de um provedor. Rode um dos dois comandos:

```bash
ANTHROPIC_API_KEY=... dotnet run --project tools/Nexus.Eval -- --suites all --planner claude:claude-sonnet-5-5 --matcher claude:claude-haiku-5-5
```

```bash
DEEPSEEK_API_KEY=... dotnet run --project tools/Nexus.Eval -- --suites all
```

Use `--limit N` para uma rodada curta antes da completa. **Depois:** registre os números no `IA.md`, seção 5, e no `ROADMAP.md` (feature 10).

### 3. Busca por significado com os embeddings da OpenAI (features 5 e 10)

A busca híbrida foi medida com o modelo local: recall@1 de 87,5%, contra 80% do BM25. Falta medir com `text-embedding-3-small` e confirmar os limiares dele (0,30 na busca e 0,50 no contexto, definidos em `LlmSettings.EmbeddingThresholdsFor`).

**Precisa de:** `OPENAI_API_KEY`.

```bash
OPENAI_API_KEY=... dotnet run --project tools/Nexus.Eval -- --embeddings openai
```

**Depois:**
- Ajuste os limiares se os resultados pedirem.
- Registre os números no `IA.md`, seção 5, e no `ROADMAP.md` (features 5 e 10).

## Melhorias opcionais (não bloqueiam a entrega)

### 4. Casos que ainda passam pela checagem de valores do Matcher

A checagem `RecipeGrounding` recusa valores que não estão no pedido. Dos 40 pedidos escritos à mão, 2 ainda passam por ela. Nos dois casos, o resultado continua sendo uma proposta que quem pediu confirma.
- "baixa 3 LIM-200 no A-01-02 por avaria", sem motivo: o modelo usa "Avaria" como motivo, e a palavra está no pedido.
- Recebimento com lote e validade: a receita `receber_na_doca` é sem lote, e o modelo a usa mesmo assim.

**Ideias:**
- Exigir que o motivo tenha mais que a palavra do próprio tipo de operação.
- Recusar a receita sem lote quando o pedido cita lote ou validade.
- Acrescentar exemplos assim ao conjunto de treino e treinar de novo.

### 5. Latência do modelo local

São cerca de 6 s por pedido numa GTX 1660 (transformers em fp16), contra 1 a 2 s do Haiku. Um caminho é servir o modelo quantizado (GGUF Q4 no llama.cpp, que também fala o formato da OpenAI em `127.0.0.1`) e medir de novo.

### 6. Treino com dados mais variados

O conjunto gerado usa poucos modelos de frase: a perda chegou perto de zero na 1ª época, e o teste gerado deu 100%. Para generalizar melhor:
- mais modelos de frase;
- números por extenso;
- SKUs fora da lista;
- mais pedidos incompletos com resposta "nenhuma receita".

Mantenha o `holdout.json` fora do treino, para continuar medindo a generalização.

### 7. Cofre do macOS

O cofre do sistema já funciona no Windows (DPAPI) e no Linux (libsecret). O Keychain do macOS ainda não é suportado (`ROADMAP.md`, feature 1).
