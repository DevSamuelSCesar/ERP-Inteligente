# Árvore do projeto

```
erp_inteligente/
├── .gitignore        # Arquivos ignorados pelo git (segredos, bancos .db, PDFs gerados, bin/ e obj/)
├── CLAUDE.md         # Orientações para o Claude Code neste repositório
├── erp_inteligente.csproj  # Projeto do app (Avalonia 12, .NET 10, MVVM Toolkit, DataGrid, Microsoft.Data.Sqlite + SQLCipher, Argon2); embute db/*.sql
├── app.manifest      # Manifesto Windows do executável
├── Program.cs        # Ponto de entrada: plataforma, Barlow como fonte padrão
├── App.axaml(.cs)    # Aplicação: tema, tokens, ícones, estilos; inicialização (caminho do banco, cofre, erro fatal)
├── AppHost.cs        # Raiz de composição do núcleo: banco, auditoria, policy engine, autenticação, sessão, memória e conhecimento (indexa os procedimentos ao abrir), receitas e Recipe Matcher, backup, manutenção de memórias e consumo de tokens (jobs diários na abertura), e shell
├── AppController.cs  # Alterna login e shell; renova a sessão com teclado/mouse e a encerra por inatividade ou saída
├── IWindowHost.cs    # Abstração da janela principal e do encerramento (adaptador do lifetime desktop)
├── StartupError.cs   # Mensagens ao operador para falhas de inicialização (chave ausente, cofre, banco ilegível)
├── ViewLocator.cs    # Resolve a View de cada ViewModel por convenção de nome
├── Assets/
│   ├── Fonts/        # Família Barlow (SIL OFL, com OFL.txt): Regular, Medium, SemiBold, Condensed, Semi Condensed
│   └── avalonia-logo.ico  # Ícone do template (não usado pela janela)
├── Styles/
│   ├── Tokens.axaml  # Tokens de cor (seção 9 do UI.md), sobrescritas do Fluent e do DataGrid, recursos de fonte
│   ├── Icons.axaml   # Ícones próprios em StreamGeometry (navegação, ações, estados)
│   ├── ButtonTheme.axaml  # ControlTheme próprio do Button (substitui o do Fluent, sem sobreposição translúcida)
│   └── Controls.axaml  # Estilos: botões, navegação, abas, réguas, quadros, DataGrid; sem sombras nem transições
├── Core/             # Núcleo das features 1 a 9 do ROADMAP (seção 12 da ARQUITETURA)
│   ├── Security/     # Cofre do SO (libsecret, DPAPI, memória), KeyManager, DatabaseKeys, ColumnCipher (AES-GCM), PasswordHasher (Argon2id)
│   ├── Data/         # NexusDatabase (3 conexões SQLCipher e OpenWithSameKey para backups), AiAuthorizer, DatabaseBootstrapper (cria o banco a partir de db/*.sql)
│   ├── Audit/        # AuditEntry, AuditHasher (regra v1), AuditLog (append encadeado), AuditVerifier
│   ├── Auth/         # AuthService (login, bloqueio, auditoria), SessionService (expiração por inatividade), LoginResult
│   ├── Policy/       # PolicyEngine (deny-overrides, interseção IA ∩ usuário), ApplicationDb (consultas com permissão e $warehouse_id)
│   ├── Engine/       # Motor de planos: DSL v1, catálogo, Schemas/v1 (JSON Schema), regras por operação (estrutura: StructureStore; pedidos: OrderStore e *InboundOrder/*OutboundOrder; reservas: ReservationStore, ReserveStock, ReleaseReservation, PickItems, ShipOrder), Simulator, Executor, PlanEngine (direitos IA ∩ usuário para planos da IA; rejeitar um plano da IA grava uma memória de correção do sistema; depois de cada decisão conta o uso da receita ou aprende com o plano executado), SkillMapping (operação → skill)
│   ├── Llm/          # Camada de provedores de LLM: ILlmProvider e modelos (LlmModels: provedor, papel, escolha), ChatCompletionsProvider (cliente comum, HTTPS fixo, retries, timeout) com DeepSeekProvider, OpenAiProvider e LocalProvider (só 127.0.0.1, sem chave), ClaudeProvider (SDK oficial Anthropic, blocos originais devolvidos no laço de ferramentas), LlmRouter (adaptador por provedor), LlmSettings (chave por provedor, provedor e modelo por papel, porta do modelo local e provedor de embeddings, no cofre do SO), EmbeddingsClient (formato embeddings da OpenAI, só api.openai.com ou 127.0.0.1) e ConfiguredEmbeddingProvider (lê a escolha de Configurações a cada chamada)
│   ├── Skills/       # Skills (feature 4): SkillDocument (parser estrito do SKILL.md, Serialize, Validate com exemplos checados pelo DslParser), SkillText (normalização), BuiltInSkills (skills/*/SKILL.md embutidos), SkillStore (CRUD auditado com versões), SkillCatalog (índice e carga para a IA via v_ai_skills), SkillModels/SkillJson/SkillIssue
│   ├── Memory/       # Memória da IA (feature 5): MemoryFilter (filtro de proteção: tamanho, CPF/CNPJ/e-mail/telefone, texto que parece instrução), MemoryStore (Remember com dedup e reforço, List, Create/Update/Archive/Reactivate auditados), MemoryRetriever (FTS5 + BM25 combinado com confiança e escopo, por armazém), MemoryModels, TextFold (minúsculas sem acento), MemoryMaintenance (feature 9: arquiva repetidas, parecidas e sem uso; diária ou pelo supervisor), MemoryFeedback (confiança sobe com plano executado e cai com plano rejeitado; erros recorrentes viram memórias `pattern`)
│   ├── Operations/   # Operação (feature 9): BackupService (backup criptografado pela API do SQLite, job diário com 7 cópias, restauração verificada), LlmUsageLog (llm_usage e relatório de custos) e LlmPrices (preços do IA.md)
│   ├── Recipes/      # Receitas (feature 7): RecipeTemplate (assinatura da estrutura, extração de parâmetros tipados, preenchimento e conversão dos valores do formulário), RecipeStore (candidata após 3 planos iguais, promoção, suspensão, reativação com revalidação e despromoção, auditados), RecipeMatcher (chamada curta ao modelo do papel Matcher antes do Planner, com filtro sem LLM; aceita o formato do fine-tuning), Prompts/matcher.md (instruções do Matcher, embutidas e lidas também pelo gerador do fine-tuning), RecipeModels
│   ├── Safety/       # Defesas da feature 6: InjectionDetector (texto com cara de instrução ao modelo; regras-base também usadas pelo MemoryFilter) e SensitiveDataRedactor (máscara de dados sensíveis na auditoria, antes do hash)
│   ├── Knowledge/    # RAG (feature 5): KnowledgeChunker (um trecho por seção ##, com sobreposição), KnowledgeIndexer (Sync idempotente de knowledge/*.md embutidos), KnowledgeRetriever (BM25 com minScore e minMatchedTerms; SearchHybridAsync: BM25 + cosseno por RRF), KnowledgeVectorIndex (vetores em knowledge_embeddings, só os que faltam; distância pelo sqlite-vec ou em C#), FtsQuery (monta MATCH seguro, compartilhado com a memória), IEmbeddingProvider/NullEmbeddingProvider (modelo, limiares e lote de vetores)
│   └── Planner/      # Planner (feature 3): Planner (laço LLM com ferramentas, propose_plan com até 3 alternativas e offer_options), PlannerTools (consultas validadas na conexão da IA), PlannerSearchShared (auxiliares das buscas: termos, paginação, teto de bytes), PlannerProductSearch / PlannerZoneSearch / PlannerLocationSearch (next_free_codes, typical_capacity) / PlannerLotSearch / PlannerOrderSearch / PlannerPartnerSearch (buscas robustas: texto livre, parcial, sem acento, filtros, ranking, paginação), PlannerModels (resultados, propostas de plano e de skill, opções e limites), load_skill e propose_skill, PlannerMemoryTools (search_knowledge, search_memories e o schema de remember), PlannerInjection (feature 6: exame dos dados externos, marca alerta="instrucao_suspeita", auditoria ai.injection), PlannerRecipes e PlannerRecipeTools (feature 7: proposta vinda do Recipe Matcher e ferramenta search_recipes) e contexto automático de memórias e procedimentos no pedido (feature 5; remember grava pelo MemoryStore, no máximo 2 por pedido), Prompts/system.md (prompt de sistema proativo embutido, só com o índice das skills)
├── Models/           # Plano, passos, status, resumo de execução, sessão, permissões e linhas das consultas
├── Services/         # Interfaces da UI com o núcleo (planos, Nexus, consultas, sessão, diálogos), ApprovalPolicy, LlmSettingsService (chaves por provedor, modelo por papel e porta do modelo local, só pelo administrador) ISkillService (skills da sessão), IMemoryService (memórias do armazém) IRecipeService (receitas) e IOperationsService (verificação da auditoria, custos de IA e backups)
│   └── Sqlite/       # Implementações reais: consultas por armazém, planos pelo motor (EnginePlanService), Nexus pelo Planner (PlannerNexusService, com o aviso de instrução suspeita), skills pelo SkillStore (SqliteSkillService), memórias pelo MemoryStore (SqliteMemoryService), receitas pelo RecipeStore (SqliteRecipeService), operação pelo BackupService e LlmUsageLog (SqliteOperationsService)
├── ViewModels/       # Login, shell e navegação, Nexus (NexusText: destaques das respostas; OptionsViewModel: opções clicáveis), propostas do Nexus com alternativas (DraftPlanViewModel), skill proposta pelo Nexus (SkillProposalViewModel), tela Skills (SkillsViewModel: lista, detalhe e editor com validação ao vivo), tela Memórias (MemoriesViewModel, com o filtro de proteção ao vivo), linha "Memória registrada" do Nexus (MemoryNoteViewModel), tela Receitas (RecipesViewModel: abas por situação, uso sem LLM com pré-visualização e envio, promoção, suspensão, reativação e despromoção pelo supervisor), Novo plano (PlanForms: formulários gerados do catálogo, listas de escolha, próximo código livre), quadro do plano, resumo visual, telas de consulta (Estoque, Produtos, Zonas, Endereços, Pedidos, Parceiros), Auditoria (AuditViewModel: filtros por categoria e problema, verificação da cadeia), Custos de IA (CostsViewModel), Planos, Configurações (estado das chaves, chave por provedor, modelo por papel, porta do modelo local, backups do administrador), diálogo
├── Views/            # Login, erro fatal, janela principal, Nexus (texto com destaques em code-behind), proposta do Nexus (Confirmar/Descartar, Por quê, Suposições), skill proposta, tela Skills (lista e detalhe ou editor), tela Memórias (com o botão Manutenção), tela Receitas (lista, detalhe com uso ou editor de promoção), Auditoria e Custos de IA, linha de memória registrada, opções, Novo plano, quadro do plano, resumo, telas de consulta (incl. Produtos, Zonas, Parceiros), Configurações (provedores de LLM e modelo por papel), diálogo de rejeição
│   └── Converters/   # IconKeyConverter (chave de ícone → geometria)
├── tests/
│   └── Nexus.Tests/  # xUnit v3 + Avalonia Headless (Skia real)
│       ├── Core/     # Testes do núcleo por módulo (incl. Skills: documento, store, catálogo e embutidas; Memory: filtro, store e retriever; Knowledge: FtsQuery, chunker, indexação e busca nos 22 documentos, HybridSearchTests (vetores, sqlite-vec na conexão criptografada, RRF, limiares, falha do provedor); Engine, Llm (DeepSeekProviderTests, MultiProviderTests, LlmSettingsTests, EmbeddingsClientTests) e Planner: PlannerTests, ProductSearchTests, SearchToolsTests, ProposePlanTests, SkillToolsTests, MemoryContextTests, PlannerNexusServiceTests, InjectionDefenseTests, RecipeMatcherTests, ModelPerRoleTests; Recipes: RecipeTemplateTests e RecipeStoreTests; Safety: InjectionDetectorTests, SensitiveDataRedactorTests e PromptInjectionSuiteTests, a suíte de 25 ataques com métricas) + TestDatabase e ManualTimeProvider; Data/Fixtures: scripts das versões 1 a 8 do schema (migrações 002 a 009); Operations: BackupServiceTests e LlmUsageTests; Memory: MemoryMaintenanceTests e MemoryFeedbackTests
│       ├── Services/ # Testes dos serviços Sqlite (consultas, planos, isolamento entre armazéns)
│       ├── Fakes/    # Serviços em memória (antigos serviços de demonstração), ScriptedLlmProvider (LLM roteirizado; os testes nunca chamam a API real) e ConceptEmbeddingProvider (embeddings determinísticos por conceito)
│       ├── TestApp.cs  # AppHost real sobre um TestDatabase, com login dos usuários do seed
│       └── *.cs      # Fluxo login/shell/expiração, UI headless (incl. proposta do Nexus), ViewModels, tela Skills e skill proposta (SkillsViewModelTests), tela Memórias e linha de memória no Nexus (MemoriesViewModelTests), tela Receitas (RecipesViewModelTests), auditoria, custos, backups e manutenção (OperationsViewModelTests), Configurações, contraste das cores, política de aprovação
├── installer/        # Instaladores (feature 9): build.ps1 (publish self-contained de arquivo único, zip e Inno Setup no Windows, tar.gz no Linux), nexus.iss (instalador por usuário), linux/ (install.sh e nexus.desktop), README.md; out/ fica fora do git
├── ROADMAP.md        # Ordem de implementação das features, da fundação à operação
├── README.md         # Entrega do grupo: problema, canvas, diagramas, UML, partes de IA, Segurança e integração
├── db/               # Banco SQLite: scripts de criação, segurança, carga e desempenho
│   ├── schema.sql    # Tabelas (STRICT), FTS5 de memórias e conhecimento, auditoria
│   ├── security.sql  # Triggers DENY (imutabilidade, anti-escalonamento), permissões efetivas, views v_ai_* e mascaradas
│   ├── seed.sql      # Carga inicial fictícia: papéis, usuários (senha demo "demo" em Argon2id), armazém, estoque, planos, memórias, auditoria com hashes v1
│   ├── volume.sql    # Gera 1 milhão de movimentos sintéticos para o benchmark
│   ├── benchmark.sql # As duas consultas lentas (kardex e FEFO) com EXPLAIN QUERY PLAN e tempo
│   ├── optimize.sql  # Índices que resolvem as consultas lentas
│   ├── migrations/   # Migrações versionadas aplicadas na abertura a bancos antigos (002: decisões de plano, senha demo; 003: catálogo ativo, direitos operacionais da IA, views v_ai_zones/v_ai_lots/v_ai_partners; 004: reservas por pedido; 005: skills versionadas e view v_ai_skills; 006: memórias por armazém, status e origem, guardas e permissão memory.manage; 007: receitas com assinatura, exemplo e falhas seguidas, guardas, recipe.manage proibido à IA e v_ai_recipes com o modelo; 008: llm_usage, só inserção; 009: knowledge_embeddings, um vetor float32 por trecho)
│   │   ├── 002_plan_decisions.sql  # Migração 1 → 2: colunas e gatilhos de rejeição de plano, senha demo do seed
│   │   ├── 003_full_catalog.sql    # Migração 2 → 3: is_active em zonas, fornecedores e clientes; permissões da IA; views v_ai_*
│   │   ├── 004_reservations.sql    # Migração 3 → 4: tabela order_reservations (estado guardado por triggers) e preenchimento das reservas do pedido semeado
│   │   ├── 005_skills.sql          # Migração 4 → 5: tabelas skills e skill_versions (imutáveis), permissão skill.manage (só supervisor), view v_ai_skills
│   │   ├── 006_memory.sql          # Migração 5 → 6: memories reconstruída (armazém, status, origem, criador; guardas de exclusão e alteração), FTS reconstruído, permissão memory.manage (só supervisor)
│   │   ├── 007_recipes.sql         # Migração 6 → 7: colunas de receita (skill, assinatura única, exemplo, falhas seguidas, origem, promoção), guardas contra apagar e alterar o modelo, recipe.manage na lista proibida da IA, v_ai_recipes com o modelo
│   │   └── 008_llm_usage.sql       # Migração 7 → 8: tabela llm_usage (consumo de tokens por pedido e papel, sem texto), índice por data e gatilhos de só inserção
│   └── tests/
│       └── test_db.py  # Testes: imutabilidade, escalonamento, authorizer da IA, SQL injection, otimização, cadeia de auditoria v1, migrações 2 → 3, 3 → 4, 4 → 5, 5 → 6, 6 → 7 e 7 → 8, reservas, skills, memórias, receitas, consumo de tokens
├── tools/            # Utilitários do repositório
│   ├── readme_to_pdf.py  # Converte o README.md (com diagramas Mermaid) em PDF via Chrome headless
│   ├── finetune/     # Experimento do Matcher (feature 8): recipes.json (receitas do conjunto), generate_dataset.py (500 pedidos sintéticos, 400/100, 15% fora do escopo, com o prompt do app), evaluate.py (exact match, F1 por campo, JSON válido, "nenhuma receita", regra de adoção), predict_claude.py (linha de base Haiku 5.5 few-shot, SDK anthropic), train_lora.py (LoRA no Qwen2.5-1.5B-Instruct, perda só na resposta, QLoRA opcional, modelo mesclado), serve_local.py (Matcher nexus-matcher e embeddings nexus-embed em 127.0.0.1, formato OpenAI), predict_local.py (previsões do modelo servido), requirements-train.txt, README.md; data/ é gerado, o modelo treinado fica fora do repositório
│   └── tests/
│       ├── test_readme_to_pdf.py  # Testes do conversor (HTML seguro, SRI, detecção do Chrome, PDF gerado)
│       └── test_finetune.py  # Testes do gerador (divisão, 15% fora do escopo, determinismo, valores preservados, prompt do app) e da avaliação (métricas e regra de adoção)
├── skills/           # Skills embutidas no app (SKILL.md com frontmatter nome/descricao/operacoes, em pt-BR): recebimento, armazenagem, separacao, expedicao, inventario, cadastros
├── knowledge/        # Base do RAG: 22 procedimentos e políticas fictícios de armazém (Markdown), embutidos no app e indexados (BM25) na abertura
│   └── README.md     # Origem e organização dos documentos
└── references/       # Especificação e conceito do projeto
    ├── ARQUITETURA.md            # Arquitetura adotada: LLM planeja em JSON, motor determinístico executa; segurança de dados
    ├── CANVAS.md                 # Canvas de uma página do Nexus: problema, entrada/saída, arquitetura em caixas, risco, MVP
    ├── DIAGRAMAS.md              # Diagramas Mermaid: arquitetura integrada, ataques, fluxo, ciclos de vida, dados
    ├── IA.md                     # LLMs escolhidos, custo por 1.000 usos, RAG, fine-tuning, riscos e métricas
    ├── NEGATIVAS_WEB.md          # O que não fazer na UI: lógica web, clichês de SaaS e de LLM; checklist de tela nativa
    ├── PROPOSTAS_ARQUITETURA.md  # Alternativas de arquitetura avaliadas antes da decisão (histórico)
    ├── SEGURANCA_DADOS.md        # Classificação de dados, matriz de papéis, ameaças, otimização, ataques testados
    ├── STACK.md                  # Stack: C#/Avalonia nativo, SQLite + SQLCipher, instruções JSON/MD, provedores de LLM
    ├── TREE.md                   # Esta árvore de arquivos com descrições
    ├── UI.md                     # Interface do Nexus: layout, chat, quadro do plano, resumo visual, visibilidade, identidade visual
    ├── UI/                       # Sketches e imagens de referência de construção da UI (não de cor nem de cópia)
    │   ├── erp_ui.jpeg           # Janela genérica: coluna de menus e container de conteúdo
    │   ├── erp_ui_chat.jpeg      # Tela do Nexus (chat)
    │   ├── erp_ui_chat_plan.jpeg # Nexus com o quadro do plano e o prompt aceitar/rejeitar
    │   └── totvs-backoffice-linha-protheus-financeiroo.png.webp  # Referência de estrutura das telas de consulta
    └── UML.md                    # UML: casos de uso, classes do domínio e do motor, componentes e implantação
```
