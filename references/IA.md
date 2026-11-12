# Inteligência Artificial

> Detalha as seções 8, 10 e 12.8 de [`ARQUITETURA.md`](ARQUITETURA.md).

## 1. LLMs escolhidos

| Papel | Modelo | Por quê |
|-------|--------|---------|
| Planner (planeja e trata exceções) | **Claude Sonnet 5.5** | Boa qualidade em tool calling com muitas etapas; suporta *strict tool use* (argumentos sempre válidos pelo schema) e controle de esforço. Custa metade do Opus 5.5 por token. |
| Receitas e extração de parâmetros | **Claude Haiku 5.5**, depois o modelo do fine-tuning | Tarefa curta e repetitiva; é o modelo mais barato entre os avaliados. |
| Embeddings do RAG | **OpenAI `text-embedding-3-small`** | Anthropic e DeepSeek não oferecem embeddings; custo desprezível. Pode ser trocado por um modelo local. |

DeepSeek e OpenAI continuam suportados pela interface `ILlmProvider` e podem ser escolhidos na configuração.

**Estado da implementação (feature 3):** o primeiro adaptador implementado é o da **DeepSeek**.
- **Modelos:** `deepseek-v4-pro` no Planner por padrão, ou `deepseek-flash`, escolhidos em Configurações. Claude e OpenAI entraram na feature 8 (abaixo).
- **Chave:** digitada pelo administrador e guardada no cofre do SO.
- **Limites por pedido:** 4 planos inválidos, 12 rodadas, 3 propostas válidas, 8.192 tokens por chamada e 60.000 por pedido. Passou do limite, a tarefa é escalada ao operador, salvo quando já há propostas válidas: essas são entregues. É a defesa contra "custo descontrolado" da seção 5.
- **Direitos:** a IA tem os direitos operacionais do usuário que pede (decisão de 2026-11-08). Ver [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), seção 3.1.
- **Recipe Matcher (feature 7):** uma chamada sem ferramentas, de até 600 tokens de resposta. Um filtro sem LLM evita a chamada quando o pedido não tem valor nem palavra em comum com uma receita ativa.
- **Multi-provedor (feature 8):** Claude (pelo SDK oficial `Anthropic`), OpenAI e um modelo local estão implementados. O administrador escolhe provedor e modelo de cada papel em Configurações:
  - Planejamento: `deepseek-v4-pro` ou `deepseek-flash`; `claude-sonnet-5-5`, `claude-opus-5-5` ou `claude-haiku-5-5`; `gpt-5` ou `gpt-5-mini`.
  - Matcher: os mesmos, mais o modelo local ajustado (`nexus-matcher`).
  - O padrão continua `deepseek-v4-pro` no planejamento e `deepseek-flash` no Matcher, para não mudar o custo de quem já usa o app. Para seguir a tabela acima, escolha Claude Sonnet 5.5 e Claude Haiku 5.5.

**Privacidade:** o que sai da máquina é sempre não sensível (views `v_ai_*`, procedimentos fictícios e memórias filtradas). Por isso a escolha do provedor afeta custo e qualidade, mas não expõe CPF, contatos ou custos. Quem quiser zero envio de dados pode usar o modelo do fine-tuning e embeddings locais para receitas e RAG; o Planner ainda depende de um provedor.

## 2. Custo por 1.000 usos

### Preços usados

Consultados em 27/10/2026, por 1 milhão de tokens:

| Modelo | Entrada | Saída | Fonte |
|--------|--------:|------:|-------|
| Claude Opus 5.5 | US$ 4,00 | US$ 20,00 | Tabela de modelos da Anthropic |
| Claude Sonnet 5.5 | US$ 2,00 | US$ 10,00 | Tabela de modelos da Anthropic |
| Claude Haiku 5.5 | US$ 0,10 | US$ 0,50 | Tabela de modelos da Anthropic (prompts até 100 mil tokens) |
| `deepseek-v4-pro` | ¥ 9,00 | ¥ 27,00 | api-docs.deepseek.com, preço de pico, sem cache |
| `deepseek-flash` | ¥ 2,00 | ¥ 8,00 | api-docs.deepseek.com, preço de pico, sem cache |
| `text-embedding-3-small` | US$ 0,02 | — | developers.openai.com |

A DeepSeek publica preços só em yuan (¥). Fora do horário de pico de Pequim, o preço cai pela metade.

### Premissas de um "uso" (um pedido do operador)

| Caminho | Chamadas | Tokens de entrada | Tokens de saída |
|---------|---------:|------------------:|----------------:|
| Planejamento completo | ~3 (consultas, `propose_plan`, texto final) | 12.000 (acumulado) | 2.000 (inclui raciocínio) |
| Receita | 1 | 1.500 | 150 |
| Embedding da pergunta | 1 | 40 | — |

No início, 70% dos pedidos passam pelo planejamento e 30% por receitas. Com o uso, a meta é chegar a 40% e 60%.

**Medição real (feature 9):** cada pedido grava seus tokens em `llm_usage`, e a tela Custos de IA mostra o custo estimado com os preços acima (Anthropic em US$, DeepSeek em ¥ no pico; modelos sem preço aqui, como os da OpenAI, aparecem só com os tokens). É a fonte para refazer esta conta com números medidos.

**Premissas em revisão:** o modo proativo (2026-11-08) pode fazer mais consultas por pedido e até 3 propostas. As premissas de 12.000 tokens de entrada e de 3 chamadas não foram medidas com esse modo. Refazer a conta com a medição do conjunto de avaliação (seção 5) antes de citar um custo real.

### Resultado

| Configuração | 70% planejamento | 40% planejamento |
|--------------|-----------------:|-----------------:|
| **Sonnet 5.5 + Haiku 5.5 (padrão)** | **≈ US$ 30,90** | **≈ US$ 17,75** |
| Opus 5.5 + Haiku 5.5 | ≈ US$ 61,70 | ≈ US$ 35,30 |
| Haiku 5.5 em tudo | ≈ US$ 1,60 | ≈ US$ 1,02 |
| `deepseek-v4-pro` + `deepseek-flash` (pico) | ≈ ¥ 114,70 | ≈ ¥ 67,30 |
| Sonnet 5.5 sem receitas (100% planejamento) | ≈ US$ 44,00 | — |

**Contas do padrão (70/30):**
- 700 × (12.000 × 2 + 2.000 × 10) / 1.000.000 = US$ 30,80
- 300 × (1.500 × 0,10 + 150 × 0,50) / 1.000.000 = US$ 0,07
- Embeddings: US$ 0,0008

**Leituras:**
- As **receitas** são o que mais reduz o custo: sair de 70% para 40% de planejamento corta ~43% do gasto.
- O **cache de prompt** (prompt de sistema + definições de ferramentas, que se repetem em todo pedido) não entrou na conta e reduz ainda mais o custo de entrada.
- Haiku em tudo é ~20× mais barato, mas fica como opção configurável: o Planner é onde erros custam mais.

## 2.1 Skills sob demanda (feature 4)

- O prompt de sistema leva só o índice das skills: uma linha por skill (nome, descrição de até 300 caracteres e operações), cerca de 60 tokens cada. As 6 skills embutidas somam ~400 tokens no índice, contra ~4.800 se os corpos fossem todos no prompt.
- O corpo (2,4 a 3,3 mil caracteres, ~800 tokens) entra só quando a IA chama `load_skill`, no máximo 3 por pedido.
- Como os exemplos de plano das skills já passam pelo `DslParser`, a IA tem modelos válidos à mão, o que reduz rodadas de correção.
- A IA também redige skills a pedido do supervisor (`propose_skill`); nada é gravado sem a confirmação dele.

## 2.2 Memórias e contexto automático (feature 5)

- Cada pedido recebe até 5 memórias do armazém e até 2 trechos de procedimento, limitados a 2.500 caracteres (~650 tokens no pior caso). Pedidos operacionais ("mova 10 do ABC-123") normalmente não recebem trecho nenhum: os identificadores saem da consulta e são exigidos 2 termos em comum.
- A IA busca mais quando precisa (`search_knowledge`, `search_memories`) e registra regras e preferências ditas pelo operador (`remember`, até 2 por pedido).

## 3. RAG

| Etapa | Definição |
|-------|-----------|
| Estado | Feature 5: BM25 em 2026-11-08; **busca híbrida em 2026-11-11** (BM25 + cosseno, RRF). Os embeddings são opcionais e ficam desligados até o administrador escolher o provedor em Configurações |
| Fontes | `knowledge/*.md` (22 documentos fictícios, embutidos e indexados na abertura). As skills não entram: são carregadas sob demanda por `load_skill` |
| Chunking | Um trecho por seção `##`, prefixado com título do documento e da seção; seções acima de ~500 tokens divididas com ~50 tokens de sobreposição |
| Embeddings | `text-embedding-3-small` (OpenAI, 1.536 dimensões) ou o modelo local `nexus-embed` (`paraphrase-multilingual-MiniLM-L12-v2`, 384 dimensões, em `127.0.0.1`, sem custo e sem envio de dados) |
| Banco vetorial | `knowledge_embeddings` no mesmo arquivo SQLite (float32, um vetor por trecho), com a distância calculada pelo `sqlite-vec` |
| Busca | Híbrida: top-20 FTS5/BM25 + top-20 por cosseno, fundidos por Reciprocal Rank Fusion. Até 2 trechos no contexto automático e até 5 em `search_knowledge`. Similaridade mínima para um trecho achado só pelo significado: 0,30 (busca) e 0,50 (contexto) na OpenAI; 0,40 e 0,55 no modelo local |
| Uso no prompt | Trechos delimitados como dados de referência, com a origem (`POP-03`, `POL-17`...) para citação |

### Base de documentos inicial

`knowledge/` tem **22 documentos** escritos pelo grupo para um centro de distribuição **fictício** (CD-01): 16 procedimentos operacionais, 4 políticas, glossário e perguntas frequentes. Não há dados de empresas ou pessoas reais. O banco de teste (`db/seed.sql`) também é fictício: nomes de pessoas genéricos, documentos sempre mascarados e colunas sensíveis vazias.

## 4. Fine-tuning

| Item | Definição |
|------|-----------|
| Tarefa | Pedido em linguagem natural → `{intent, recipe, params}`, ou `{"intent":"none"}` |
| Dados | ~500 pedidos fictícios gerados a partir das receitas e do catálogo de teste (variações de escrita, abreviações, erros de digitação, 15% fora do escopo); anotados e revisados pelo grupo; 400 treino / 100 teste |
| Modelo base | Modelo aberto pequeno com LoRA (classe 1–3 B parâmetros, ex.: Qwen2.5-1.5B-Instruct), rodando local |
| Métricas | *Exact match* do JSON; F1 por campo; % de JSON válido; acerto em "nenhuma receita" |
| Linha de base | Claude Haiku 5.5 com *few-shot* no mesmo conjunto de teste |
| Critério de adoção | Substitui o Haiku se empatar ou superar no *exact match* e tiver ≥ 99% de JSON válido |
| Ferramentas (feature 8) | `tools/finetune`, ver o `README.md` de lá. O gerador e a avaliação estão testados (`tools/tests/test_finetune.py`). **Resultados ainda não medidos:** o treino precisa de GPU e a linha de base precisa da chave da Anthropic |

Exemplo de par de treino:

```json
{"input": "guarda as 20 cx de ABC-124 da doca 2 no A-01-02",
 "output": {"intent": "putaway", "recipe": "armazenar_da_doca",
            "params": {"sku": "ABC-124", "qty": 20, "dock": "DOCA-02", "target": "A-01-02"}}}
```

## 5. Onde a IA pode errar e como medimos

| Risco | Exemplo | Defesa | Métrica (conjunto de avaliação) |
|-------|---------|--------|---------------------------------|
| Alucinação de dados | Inventar um endereço ou SKU que não existe | O validador rejeita referências inexistentes | % de planos válidos na 1ª tentativa; nº médio de tentativas (50 pedidos com gabarito) |
| Alucinação de procedimento | Afirmar uma regra que não está nos documentos | RAG com citação obrigatória da origem | % de respostas corretas e com fonte certa (40 perguntas sobre `knowledge/`); recall@4 do RAG |
| Fora do escopo | "Calcule a folha de pagamento", "qual a previsão do tempo?" | Prompt de sistema restrito ao armazém; sem ferramentas para outros assuntos | % de recusas corretas (30 pedidos fora do escopo) |
| Recusa ou pergunta desnecessária | "Guarde 10 cx do ABC-123 em Q-01-01" com a zona Q inexistente: o certo é propor a zona, os endereços e a movimentação | Prompt proativo: padrões, suposições e plano que cria o que falta; pergunta só com `offer_options` | % de pedidos com plano válido sem pergunta (casos proativos); % de recusas corretas (sem direito ou fora do escopo) |
| Prompt injection | Ver [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), seção 6.1 | Seção 12.8 da arquitetura | Taxa de sucesso do ataque (meta 0%) e taxa de detecção (25 casos) |
| Custo descontrolado | Laço de correções de plano | Limite de tentativas e de tokens por tarefa | Tokens médios por pedido; % de pedidos resolvidos por receita (medidos no uso real pela tela Custos de IA, feature 9) |

**Menos recusas são esperadas.** O modo proativo reduz as recusas de propósito, então o conjunto de avaliação deve incluir casos proativos: entidade inexistente, dado faltando e pedido sem direito. Sem esses casos, uma queda na recusa pareceria melhoria.

Os conjuntos de avaliação são fictícios, versionados junto com o código e rodados a cada mudança de prompt, skill ou modelo.
