# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

**Nexus** é um ERP cuja premissa central é ser **totalmente gerenciável por LLMs**: toda operação do sistema deve poder ser descoberta, executada e auditada por um agente de IA, não apenas por uma interface humana.

## Estado atual

A stack está definida em `references/STACK.md` (app desktop nativo em C# com Avalonia, SQLite, instruções de LLM em JSON/Markdown, provedores DeepSeek, Claude e OpenAI). A documentação no código (comentários, XML docs, identificadores, logs) é em inglês. A arquitetura adotada está em `references/ARQUITETURA.md`: o LLM lê dados por tool calling, mas só altera dados enviando um plano JSON (`propose_plan`), que um motor determinístico valida, simula e executa numa transação; planos bem-sucedidos viram receitas reutilizáveis sem LLM. `references/PROPOSTAS_ARQUITETURA.md` guarda as alternativas avaliadas. O banco é só SQLite (SQLCipher, FTS5, `sqlite-vec`); SQL Server, MongoDB e n8n não fazem parte do projeto.

O app C# (Avalonia, MVVM com CommunityToolkit.Mvvm) tem a interface completa e o **núcleo da feature 1** do `ROADMAP.md`. Os módulos do núcleo ficam em `Core/`:
- `Security`: cofre do SO, chaves, Argon2id, AES-GCM.
- `Data`: SQLCipher, as três conexões, o *authorizer* da IA e o bootstrap a partir de `db/*.sql` embutidos.
- `Audit`: cadeia de hash v1.
- `Auth`: login, bloqueio e sessão.
- `Policy`: permissões e escopo por armazém.
- `Engine`: motor de planos (feature 2), com 20 operações do catálogo DSL v1; `adjust_stock`, `cancel_inbound_order`, `ship_order` e `cancel_outbound_order` são críticas.
  - DSL v1 (`DslParser`) e JSON Schemas em `Core/Engine/Schemas/v1`.
  - Regras por operação (`*Handler`), `PlanSimulator` (rollback), `PlanExecutor` e `PlanEngine` (máquina de estados).
  - Planos com origem `ai` rodam com os direitos IA ∩ usuário. A IA tem os direitos operacionais do usuário (decisão de 2026-11-08): toda proposta exige Confirmar de quem pediu e críticos exigem outro supervisor; aprovar, ler dados sensíveis, gerir usuários e papéis e ler a auditoria continuam proibidos.
- `Llm` (features 3 e 8): `ILlmProvider` e os adaptadores:
  - `DeepSeekProvider`, `OpenAiProvider` e `LocalProvider` (só `127.0.0.1`, sem chave, para o Matcher ajustado por fine-tuning) herdam de `ChatCompletionsProvider` (URL fixa, sem redirecionamento);
  - `ClaudeProvider` usa o SDK oficial `Anthropic` (NuGet), devolve os blocos originais da resposta em `ProviderState` (thinking assinado volta inalterado no laço de ferramentas) e junta resultados de ferramenta seguidos numa só mensagem;
  - `LlmRouter` escolhe o adaptador por `LlmRequest.Provider` (nulo = DeepSeek);
  - `LlmSettings` guarda no cofre do SO uma chave por provedor, o provedor e o modelo de cada papel (`LlmUseRole.Planner` e `Matcher`; o modelo local só serve ao Matcher), a porta do modelo local e o provedor de embeddings (`EmbeddingChoice`: desligado, OpenAI `text-embedding-3-small` ou local `nexus-embed`). Os membros sem provedor continuam sendo os da DeepSeek. Nos testes, a chave nunca vai para a API real: há `HttpMessageHandler` falso;
  - `EmbeddingsClient` (formato embeddings da OpenAI, só `api.openai.com` ou `127.0.0.1`) e `ConfiguredEmbeddingProvider`, que lê a escolha a cada chamada.
- `tools/finetune`: experimento do Matcher (feature 8), com gerador do conjunto sintético (lê o mesmo `Core/Recipes/Prompts/matcher.md` que o app embute), avaliação (*exact match*, F1 por campo, JSON válido, "nenhuma receita" e a regra de adoção), linha de base com Haiku 5.5 (SDK Python `anthropic`), treino LoRA (`train_lora.py`: perda só na resposta, QLoRA opcional, modelo mesclado) e `serve_local.py`, que serve o Matcher (`nexus-matcher`) e os embeddings (`nexus-embed`) em `127.0.0.1` no formato da OpenAI. O ambiente de treino fica fora do repositório.
- `Planner`: laço de tool calling com 8 ferramentas de leitura sobre `v_ai_*` (`search_*` e `get_*`), `load_skill` (corpo sob demanda, até 3), `propose_plan` (até 3 alternativas, só simula), `propose_skill` (só para quem tem `skill.manage`), `offer_options` (botões) e o prompt de sistema em `Core/Planner/Prompts/system.md`, que leva só o índice das skills.
- `Skills` (feature 4): `SkillDocument` (formato `SKILL.md` em pt-BR: `nome`, `descricao`, `operacoes`), `BuiltInSkills` (as 6 de `skills/`, embutidas), `SkillStore` (banco, versões, auditoria, exige `skill.manage`) e `SkillCatalog` (índice e carga para a IA via `v_ai_skills`). A tela Skills mostra tudo; só o supervisor cria e edita.
- `Operations` (feature 9):
  - `BackupService`: backup pela API do SQLite com a mesma chave (`NexusDatabase.OpenWithSameKey`), job diário com 7 cópias, restauração verificada (chave, versão, integridade, cadeia de auditoria) com cópia de segurança antes. Só com `user.manage`.
  - `LlmUsageLog`: grava `llm_usage` (o Planner e o Matcher) e monta o relatório de custos (`audit.read`), com os preços de `LlmPrices` (os do `IA.md`; modelo sem preço não ganha custo inventado).
  - `Core/Memory/MemoryMaintenance`: arquiva repetidas, parecidas e paradas; diário ou pelo supervisor.
  - Os jobs diários rodam em `AppHost.Open` (`runScheduledJobs`); os testes não os disparam.
- `Core/Memory/MemoryFeedback`: as memórias que a IA tinha à frente ficam na auditoria `plan.submit`; plano executado dá +0,05 e plano rejeitado −0,15 a cada uma. O mesmo código de erro 3 vezes em 30 dias no armazém (validador no Planner ou plano que falhou) vira uma memória `pattern` de origem `system`. O `PlanEngine` e o Planner recebem o `MemoryFeedback`.
- `Recipes` (feature 7): `RecipeTemplate` (assinatura `op(arg,...)` por passo, extração de parâmetros tipados pelos schemas da DSL; em um modelo, `"$nome"` é parâmetro quando `nome` está no schema de parâmetros, senão é referência a passo), `RecipeStore` (candidata após 3 planos iguais executados, promoção e gestão só com `recipe.manage`, suspensão por versão de DSL/skill, despromoção após 3 falhas seguidas) e `RecipeMatcher` (chamada curta ao `deepseek-flash` antes do Planner, com filtro barato antes). O `PlanEngine` recebe o `RecipeStore` e, depois de cada decisão, conta o uso da receita ou aprende com o plano executado. A tela Receitas usa receitas sem LLM.
- `Safety` (feature 6): `InjectionDetector` (regras-base compartilhadas com o `MemoryFilter` e regras extras para dados: ordens à IA, comandos destrutivos e SQL) e `SensitiveDataRedactor` (máscara de CPF, CNPJ, e-mail, telefone, chaves e tokens, aplicada pelo `AuditLog` antes do hash). O Planner examina os dados externos (`PlannerInjection.cs`), marca o bloco com `alerta="instrucao_suspeita"`, audita `ai.injection` e devolve `Suspicious`, que vira aviso no chat. A suíte de 25 ataques fica em `tests/Nexus.Tests/Core/Safety`.
- `Memory` e `Knowledge` (feature 5): `MemoryFilter` (protege a gravação), `MemoryStore` (por armazém, arquiva em vez de apagar; `memory.manage` só para o supervisor), `MemoryRetriever` e `KnowledgeRetriever` (FTS5 na conexão da aplicação, SQL constante, `FtsQuery` escapa os termos), `KnowledgeIndexer` (indexa `knowledge/*.md` embutidos na abertura). O Planner injeta memórias e trechos relevantes como `<dados>` e tem `search_knowledge`, `search_memories` e `remember`.
  - Busca híbrida: `KnowledgeVectorIndex` guarda um vetor por trecho em `knowledge_embeddings` (só os que faltam; troca de modelo gera todos) e calcula a distância pelo `sqlite-vec` (NuGet `sqlite-vec`, carregado só na conexão da aplicação) ou em C#; `KnowledgeRetriever.SearchHybridAsync` funde BM25 e cosseno por RRF, com a pergunta mascarada e limiares por modelo. Sem embeddings, é a busca BM25. Nos testes, `ConceptEmbeddingProvider` (`tests/Nexus.Tests/Fakes`) ou um `HttpMessageHandler` falso.

Migrações: `003_full_catalog` (catálogo ativo, `order.manage`, direitos operacionais da IA e as views `v_ai_zones`, `v_ai_lots`, `v_ai_partners`), `004_reservations` (`order_reservations`) `005_skills` (`skills`, `skill_versions`, `skill.manage`, `v_ai_skills`) `006_memory` (memórias por armazém, com status e origem, e `memory.manage`) `007_recipes` (colunas de receita, guarda contra apagar e alterar o modelo, `recipe.manage` na lista proibida da IA e `v_ai_recipes` com o modelo) `008_llm_usage` (consumo de tokens por pedido, só inserção) e `009_knowledge_embeddings` (vetores dos trechos); o schema está na versão 9.

`AppHost` compõe o núcleo e `AppController` alterna entre login e shell. As telas leem o banco real por `Services/Sqlite/` (`EnginePlanService` liga a UI ao motor e `PlannerNexusService` liga o chat ao Planner). Planos são montados na tela "Novo plano" ou propostos pelo Nexus. Uma proposta só é gravada quando quem pediu clica em Confirmar. A chave da DeepSeek é digitada pelo administrador em Configurações. Os testes nunca chamam a API real: usam `ScriptedLlmProvider` (`tests/Nexus.Tests/Fakes`) e um `HttpMessageHandler` falso.

Mudanças de schema exigem uma migração em `db/migrations/NNN_*.sql`, além de `schema.sql`/`security.sql`:
- registre a migração em `DatabaseBootstrapper.Migrations` e aumente `SchemaVersion`;
- teste a migração sobre os scripts da versão anterior (`tests/Nexus.Tests/Core/Data/Fixtures`, extraídos do git). Os antigos serviços de demonstração viraram *fakes* de teste em `tests/Nexus.Tests/Fakes/`. A senha de demonstração dos usuários do seed é `demo`. Já existem os scripts SQLite em `db/` (aplicar na ordem `schema.sql` → `security.sql` → `seed.sql`; `volume.sql`, `benchmark.sql` e `optimize.sql` são da medição de desempenho) e os testes Python que os validam. `db/tests/test_db.py` também contém o *authorizer* de referência da conexão da IA, que o app C# deve reproduzir. Os dados do repositório são sempre fictícios.

## Comandos

```bash
python3 -m unittest discover -s db/tests -v                       # todos os testes de banco
python3 -m unittest db.tests.test_db.SqlInjectionTests -v          # uma classe (rodar da raiz)
python3 -m unittest db.tests.test_db.AiConnectionTests.test_ai_cannot_write
sqlite3 /tmp/erp.db < db/schema.sql && sqlite3 /tmp/erp.db < db/security.sql && sqlite3 /tmp/erp.db < db/seed.sql
python3 tools/readme_to_pdf.py -o /tmp/README.pdf                  # README → PDF (Chrome/Chromium headless)
python3 -m unittest discover -s tools/tests -v                     # testes do conversor e do fine-tuning
python3 tools/finetune/generate_dataset.py                         # conjunto do Matcher: 400 treino / 100 teste
./installer/build.ps1 -Runtimes win-x64,linux-x64                  # instaladores (installer/out, fora do git)
python3 tools/finetune/evaluate.py --gold tools/finetune/data/test.jsonl --pred P.jsonl --baseline B.jsonl
python3 tools/finetune/train_lora.py --data tools/finetune/data --out DIR/nexus-matcher   # GPU (venv com requirements-train.txt)
python3 tools/finetune/serve_local.py --matcher DIR/nexus-matcher/merged --port 8080      # Matcher e embeddings em 127.0.0.1
dotnet build                                                       # app (sem avisos)
dotnet run --project tools/Nexus.Eval                             # conjuntos de avaliação: RAG sem chave; --suites all com a chave em variável de ambiente
dotnet run                                                         # abre o app (banco em ~/.local/share/Nexus/nexus.db)
dotnet run -- --db /tmp/nexus.db                                   # banco alternativo (criado no primeiro uso, chaves no cofre do SO)
dotnet test tests/Nexus.Tests                                      # testes do app: ViewModels, serviços, contraste, UI headless
dotnet test tests/Nexus.Tests --filter "FullyQualifiedName~PlanBoard"  # filtro por nome
```

PDFs são ignorados pelo git (`*.pdf`); nunca os adicione ao repositório. Os diagramas Mermaid do `README.md` são cópias dos de `references/DIAGRAMAS.md` (seções 1 e 2) e `references/UML.md`; ao alterar um, altere o outro.

Os testes do app usam xUnit v3 + `Avalonia.Headless.XUnit` com Skia real, então carregam o XAML, os estilos e as fontes de verdade.
- `TestDatabase` cria um banco SQLCipher temporário com o cofre em memória, e `TestApp` monta o `AppHost` real sobre ele.
- O teste do libsecret usa o cofre real e se pula quando o Secret Service não está disponível.
- A regra de hash da auditoria tem implementação de referência em Python (`db/tests/test_db.py`) e em C# (`Core/Audit`), com vetores cruzados nos testes: ao mudar uma, mude a outra. `PaletteContrastTests` lê `Styles/Tokens.axaml` e falha se um par de cores sair da tabela WCAG do `UI.md`; ao mudar uma cor, atualize os dois.

## Documento de entrega

O `README.md` da raiz é a entrega acadêmica do grupo ERP INTELIGENTE (disciplinas de IA e de Segurança/Otimização de BD) e resume `references/`. Ao mudar arquitetura, segurança, custos ou números de benchmark, atualize também o `README.md`, `references/IA.md` e `references/SEGURANCA_DADOS.md`.

## `references/`

- `references/` é a especificação e o conceito do projeto. Leia antes de qualquer trabalho; se código e referências divergirem, siga as referências e aponte a divergência.
- `references/TREE.md` mantém a árvore de arquivos do projeto com uma descrição curta de cada item. Atualize-o sempre que arquivos ou diretórios forem criados, removidos, renomeados ou mudarem de propósito.
- `references/DIAGRAMAS.md` traz os diagramas Mermaid da arquitetura; mantenha-o coerente com `ARQUITETURA.md`.
- `references/UI.md` define layout e identidade visual; `references/NEGATIVAS_WEB.md` lista o que não fazer (lógica web, clichês de SaaS e de LLM). Toda tela nova passa pela checklist de `NEGATIVAS_WEB.md`.
- Não crie novos documentos de especificação sem solicitação.
