# Roadmap

Ordem de implementação das features do Nexus, derivada de [`references/ARQUITETURA.md`](references/ARQUITETURA.md). Cada feature só começa quando a anterior está testada. Sistemas interligados entram juntos na mesma feature.

> Já pronto: schema, segurança, seed e testes do SQLite em `db/`, e a base de conhecimento em `knowledge/`.

## Fundação

### 1. Núcleo da aplicação e acesso ao banco ✅
Solução C#, app Avalonia (MVVM) com login e sessão, SQLCipher com chaves no cofre do SO, as três conexões (IA somente leitura com *authorizer*, Executor, Aplicação), policy engine (permissão + escopo por armazém) e auditoria encadeada por hash com verificação na inicialização.
*Por que junto:* login, permissões, conexões e auditoria são a base de segurança de tudo que vem depois.
*Estado:* pronto. Código em `Core/`, telas lendo o banco real por `Services/Sqlite/`. O cofre do macOS (Keychain) ainda não é suportado.

### 2. Motor determinístico de planos ✅
DSL JSON versionada, Validator (JSON Schema + regras de negócio), Simulator (diff e risco), Approval (segregação de funções) e Executor transacional. Operações iniciais: `receive_goods`, `move_stock`, `create_product`, `create_location`. Operável sem LLM, por testes e por uma tela de pré-visualização do plano.
*Por que junto:* Validator, Simulator, Approval e Executor formam um pipeline único e o catálogo de operações define os três.
*Estado:* pronto.
- Código em `Core/Engine`; tela "Novo plano"; aprovação e rejeição em Planos.
- `adjust_stock` foi antecipado da feature 4, para a aprovação funcionar de ponta a ponta.
- O banco ganhou a migração versionada 1 → 2 (`db/migrations/`).

## Camada de IA

### 3. Planner com tool calling ✅
`ILlmProvider` com o primeiro adaptador, ferramentas de leitura sobre as views `v_ai_*`, `propose_plan` com devolução de erros estruturados e limite de tentativas, e tela de chat com prévia do plano e confirmação.
*Por que junto:* o Planner só tem valor com as ferramentas de leitura e a tela de chat que o alimentam e exibem seu resultado.
*Estado:* pronto, só com a **DeepSeek**. Claude e OpenAI ficam para a feature 8. Redesenhado em 2026-11-08 para o modo proativo: 8 ferramentas de leitura e pesquisa, `propose_plan` com até 3 alternativas, `offer_options` com botões de próximo passo, e formulários do Novo plano gerados do catálogo.
- Código em `Core/Llm` (provedor, chave no cofre do SO) e `Core/Planner` (laço de ferramentas, prompt de sistema).
- A chave é digitada pelo administrador em Configurações.
- O plano da IA volta como proposta: só é gravado quando quem pediu confirma.
- O motor aplica os direitos IA ∩ usuário a todo plano da IA.

### 4. Skills ✅
Skills no formato dos agentes de IA (`SKILL.md` com frontmatter `nome`, `descricao`, `operacoes`), com índice no prompt e corpo carregado sob demanda (`load_skill`). Skills `recebimento`, `armazenagem`, `separacao`, `expedicao`, `inventario` e `cadastros`.
*Por que junto:* cada skill depende das operações que ela permite.
*Estado:* concluída em 2026-11-08.
- O catálogo completo de operações (20) foi antecipado.
- As 6 skills vêm embutidas no app. Skills criadas ficam no banco, com versões e auditoria (migração 005).
- A tela Skills é somente leitura, exceto para o supervisor (`skill.manage`), que cria, edita, personaliza uma embutida, arquiva e reativa.
- A IA redige skills quando pedem (`propose_skill`), e só o Confirmar de quem pediu grava.
- Desvio do plano original: as operações declaradas não filtram as ferramentas (ver `ARQUITETURA.md`, seção 9).

### 5. Memória e RAG ✅
Tabela `memories` com filtro de proteção, busca FTS5 por escopo e confiança; indexação de `knowledge/` com embeddings e `sqlite-vec`, busca híbrida (BM25 + vetor + RRF) e injeção dos trechos como dados delimitados.
*Por que junto:* os dois são recuperação de texto livre no mesmo contexto do Planner e compartilham índices e delimitação anti-injection.
*Estado:* concluída em 2026-11-08 com BM25; a busca híbrida com embeddings entrou em 2026-11-11.
- Memórias por armazém, com status, origem e autor (migração 006). A IA grava pelo filtro (`remember`), e a rejeição de um plano da IA vira uma correção. Só o supervisor (`memory.manage`) cria, corrige e arquiva, na tela Memórias.
- `knowledge/` é indexado na abertura. Memórias e trechos relevantes entram no pedido como dados delimitados, e há as ferramentas `search_knowledge` e `search_memories`.
- **Confiança pelos resultados (2026-11-11, `Core/Memory/MemoryFeedback`):** as memórias que a IA tinha à frente ao propor um plano ficam registradas com ele (na auditoria `plan.submit`). Plano executado: +0,05 em cada uma. Plano rejeitado pelo supervisor: −0,15, o que tira de uso uma memória nova da IA (o contexto só usa confiança ≥ 0,3) até ela ser reforçada.
- **Memórias de erros recorrentes (2026-11-11):** o mesmo código de erro 3 vezes em 30 dias no armazém (proposta recusada pelo validador no Planner ou plano que falhou) vira uma memória `pattern` de origem `system`, uma por código.
- O job de manutenção de memórias foi feito na feature 9.
- **Busca híbrida (2026-11-11):**
  - migração 009 (schema na versão 9): `knowledge_embeddings`, um vetor float32 por trecho, que sai junto com o trecho;
  - `Core/Knowledge/KnowledgeVectorIndex` gera só os vetores que faltam (trecho novo ou alterado, ou modelo trocado) e calcula a distância pelo `sqlite-vec` (NuGet oficial), com o mesmo cálculo em C# se a extensão não carregar;
  - `KnowledgeRetriever.SearchHybridAsync` funde o top-20 do BM25 e o top-20 por cosseno por RRF; o contexto automático e `search_knowledge` usam a busca híbrida, e um trecho achado só pelo significado precisa passar do limiar do modelo;
  - em Configurações, "Busca por significado": desligada (padrão), OpenAI `text-embedding-3-small` ou o modelo local `nexus-embed` (servido por `tools/finetune/serve_local.py`), com o botão "Indexar procedimentos";
  - desvio: tabela comum em vez da virtual `vec0` (22 documentos não pedem índice, e o arquivo segue legível sem a extensão).
- **Pendente (depende de dados do usuário):** medir a busca com a OpenAI, que precisa da chave dela. O modelo local foi medido nos 22 documentos e define os limiares dele.

## Segurança e economia

### 6. Defesas de prompt injection e suíte de ataques ✅
Delimitação de conteúdo externo no prompt, relato de instruções suspeitas, filtro de logs sensíveis e a suíte automatizada de prompt injection e SQL injection com métricas.
*Entra antes das receitas para que o que for promovido já nasça validado contra ataques.*
*Estado:* concluída em 2026-11-11.
- `Core/Safety/InjectionDetector` examina resultados de ferramentas, memórias e trechos do RAG antes de irem ao LLM. Um bloco suspeito ainda é entregue, mas marcado com `alerta="instrucao_suspeita"`, auditado como `ai.injection` (fonte, linha e regra, nunca o texto) e avisado ao operador no chat. O pedido digitado também é examinado e só auditado. As regras-base são as mesmas do filtro de memórias.
- `Core/Safety/SensitiveDataRedactor` mascara CPF, CNPJ, e-mail, telefone, chaves de API, tokens e pares `senha=` no alvo e nos detalhes da auditoria antes do hash.
- Suíte de 25 ataques (`PromptInjectionSuiteTests`) com o modelo roteirizado no pior caso (obedece à injeção): **0% de sucesso e 100% de detecção**. Sem falso positivo nos 22 documentos de `knowledge/` nem nos produtos do seed (exceto `TST-INJ-01`).

### 7. Receitas ✅
Promoção de planos repetidos a receitas parametrizadas, Recipe Matcher com modelo barato, disparo direto pela UI sem LLM, invalidação por versão da DSL/skill e despromoção por falhas.
*Estado:* concluída em 2026-11-11.
- Código em `Core/Recipes`: `RecipeTemplate` (assinatura da estrutura, extração do modelo e dos parâmetros, preenchimento), `RecipeStore` (aprendizado, promoção, uso, suspensão e despromoção, tudo auditado como `recipe.*`) e `RecipeMatcher`.
- **Aprendizado:** 3 planos executados com a mesma estrutura (fora de receitas) viram uma receita **candidata**. Cada valor literal vira um parâmetro tipado pelo schema da operação.
- **Promoção:** só o supervisor (`recipe.manage`; a IA é barrada por trigger) dá nome e descrição. A descrição passa pelo detector de injeção, porque entra no prompt do Matcher.
- **Uso sem LLM:** na tela Receitas, quem tem `plan.submit` preenche os valores, pré-visualiza e envia. O plano vai ao motor com origem `recipe` e `recipe_id`.
- **Recipe Matcher:** antes do Planner, uma chamada ao `deepseek-flash`, sem ferramentas, escolhe a receita e extrai os valores. Ela só acontece quando o pedido tem um valor e uma palavra em comum com alguma receita ativa, e nunca para pedido suspeito de injeção. Na dúvida, segue o Planner normal. O Planner também ganhou `search_recipes`.
- **Invalidação:** a receita é suspensa quando muda a versão da DSL ou da skill, e o supervisor a reativa revalidando os valores de exemplo. 3 falhas seguidas (rejeição conta) despromovem a receita.
- Migração 007 (schema na versão 7).
- O modelo por papel configurável e o experimento de fine-tuning do Matcher foram feitos na feature 8.

## Evolução

### 8. Multi-provedor e fine-tuning ✅
Adaptadores Claude e OpenAI (a DeepSeek entrou na feature 3), modelo por papel configurável e experimento de fine-tuning do Matcher, que só substitui o Haiku se empatar ou superar no *exact match*.
*Estado:* código pronto em 2026-11-11; o treino e a comparação com o Haiku dependem de GPU e de chave e ainda não foram rodados.
- **Provedores:**
  - Claude (Anthropic), pelo SDK oficial `Anthropic`, com o thinking assinado devolvido no laço de ferramentas;
  - OpenAI (`max_completion_tokens`);
  - modelo local (servidor compatível com OpenAI, só em `127.0.0.1`, sem chave).
  - DeepSeek e OpenAI dividem o mesmo cliente de *chat completions*. Um roteador manda cada pedido ao provedor certo.
- **Modelo por papel:** em Configurações, o administrador guarda uma chave por provedor e escolhe provedor e modelo do planejamento e do Matcher. O modelo local só serve ao Matcher. O padrão continua DeepSeek nos dois papéis, então quem não mexe nada não percebe a mudança.
- **Fine-tuning (`tools/finetune`):**
  - gerador do conjunto sintético (500 pedidos, 400/100, 15% fora do escopo, com o mesmo prompt de sistema do app);
  - avaliação com a regra de adoção (empata ou supera o Haiku no *exact match* e tem ≥ 99% de JSON válido);
  - linha de base com Haiku 5.5 *few-shot*, treino LoRA do Qwen2.5-1.5B-Instruct e previsões do modelo servido localmente.
  - O Matcher aceita o formato de resposta do conjunto (`{intent, recipe, params}`).
- **Pendente:** rodar o treino (precisa de GPU) e a linha de base (precisa da chave da Anthropic), e registrar os números no `IA.md`.

### 9. Operação ✅
Backup criptografado pela API do SQLite, job de manutenção de memórias, painel de auditoria e de custos de tokens, e instaladores por plataforma.
*Estado:* concluída em 2026-11-11.
- **Backup (`Core/Operations/BackupService`):**
  - a API de backup do SQLite copia o banco aberto para `backups/`, ao lado dele, com a mesma chave do SQLCipher, então a cópia continua criptografada;
  - um backup automático por dia na abertura do app, e ficam os 7 mais recentes;
  - o administrador faz e restaura backups em Configurações. A restauração confere chave, versão, integridade e cadeia de auditoria, salva antes o estado atual e registra a restauração na auditoria.
- **Manutenção de memórias (`Core/Memory/MemoryMaintenance`):** uma vez por dia, ou pelo botão do supervisor em Memórias, arquiva:
  - memórias repetidas;
  - parecidas (≥ 80% das palavras em comum, mesmo tipo e escopo);
  - sem uso: da IA, nunca usadas, depois de 30 dias; da IA ou do sistema, paradas há 180 dias.
  - As do supervisor nunca são arquivadas pelo job, e nada é apagado.
- **Consumo de tokens:** migração 008, tabela `llm_usage`, só inserção. O Planner e o Matcher gravam uma linha por pedido, com provedor, modelo, chamadas e tokens, nunca o texto.
- **Telas (`audit.read`):**
  - **Auditoria:** filtros por categoria e por problemas, mais a verificação da cadeia;
  - **Custos de IA:** pedidos, % resolvida por receita, tokens por pedido e custo estimado por papel e modelo, com os preços do `IA.md`.
- **Link para o plano (2026-11-11):** o quadro do plano no Nexus, em Novo plano e em Receitas tem "Ver plano N em Planos", que abre a tela Planos com o plano selecionado (`UI.md`, seção 6).
- **Instaladores (`installer/`):** build *self-contained* de arquivo único para Windows (zip e instalador Inno Setup por usuário) e Linux (tar.gz com `install.sh` e atalho). macOS fica de fora até o Keychain ser suportado.

## Resumo

```
1 Núcleo + acesso → 2 Motor de planos → 3 Planner + chat → 4 Skills
   → 5 Memória + RAG → 6 Defesas e ataques → 7 Receitas → 8 Multi-provedor + fine-tuning → 9 Operação
```

O MVP do [`CANVAS.md`](references/CANVAS.md) corresponde às features 1 a 3, às skills `recebimento` e `armazenagem` da 4, ao RAG da 5 e ao núcleo da 6. Receitas, fine-tuning, inventário e expedição ficam fora dele.
