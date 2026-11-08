# Árvore do projeto

```
erp_inteligente/
├── .gitignore        # Arquivos ignorados pelo git (segredos, bancos .db, PDFs gerados, bin/ e obj/)
├── CLAUDE.md         # Orientações para o Claude Code neste repositório
├── erp_inteligente.csproj  # Projeto do app (Avalonia 12, .NET 10, MVVM Toolkit, DataGrid, Microsoft.Data.Sqlite + SQLCipher, Argon2); embute db/*.sql
├── app.manifest      # Manifesto Windows do executável
├── Program.cs        # Ponto de entrada: plataforma, Barlow como fonte padrão
├── App.axaml(.cs)    # Aplicação: tema, tokens, ícones, estilos; inicialização (caminho do banco, cofre, erro fatal)
├── AppHost.cs        # Raiz de composição do núcleo: banco, auditoria, policy engine, autenticação, sessão e shell
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
├── Core/             # Núcleo das features 1 a 3 do ROADMAP (seção 12 da ARQUITETURA)
│   ├── Security/     # Cofre do SO (libsecret, DPAPI, memória), KeyManager, DatabaseKeys, ColumnCipher (AES-GCM), PasswordHasher (Argon2id)
│   ├── Data/         # NexusDatabase (3 conexões SQLCipher), AiAuthorizer, DatabaseBootstrapper (cria o banco a partir de db/*.sql)
│   ├── Audit/        # AuditEntry, AuditHasher (regra v1), AuditLog (append encadeado), AuditVerifier
│   ├── Auth/         # AuthService (login, bloqueio, auditoria), SessionService (expiração por inatividade), LoginResult
│   ├── Policy/       # PolicyEngine (deny-overrides, interseção IA ∩ usuário), ApplicationDb (consultas com permissão e $warehouse_id)
│   ├── Engine/       # Motor de planos: DSL v1, catálogo, Schemas/v1 (JSON Schema), regras por operação (estrutura: StructureStore; pedidos: OrderStore e *InboundOrder/*OutboundOrder; reservas: ReservationStore, ReserveStock, ReleaseReservation, PickItems, ShipOrder), Simulator, Executor, PlanEngine (direitos IA ∩ usuário para planos da IA), SkillMapping (operação → skill)
│   ├── Llm/          # Camada de provedores de LLM: ILlmProvider e modelos (LlmModels), DeepSeekProvider (HTTPS fixo, retries, timeout), LlmSettings (chave e modelo no cofre do SO)
│   ├── Skills/       # Skills (feature 4): SkillDocument (parser estrito do SKILL.md, Serialize, Validate com exemplos checados pelo DslParser), SkillText (normalização), BuiltInSkills (skills/*/SKILL.md embutidos), SkillStore (CRUD auditado com versões), SkillCatalog (índice e carga para a IA via v_ai_skills), SkillModels/SkillJson/SkillIssue
│   └── Planner/      # Planner (feature 3): Planner (laço LLM com ferramentas, propose_plan com até 3 alternativas e offer_options), PlannerTools (consultas validadas na conexão da IA), PlannerSearchShared (auxiliares das buscas: termos, paginação, teto de bytes), PlannerProductSearch / PlannerZoneSearch / PlannerLocationSearch (next_free_codes, typical_capacity) / PlannerLotSearch / PlannerOrderSearch / PlannerPartnerSearch (buscas robustas: texto livre, parcial, sem acento, filtros, ranking, paginação), PlannerModels (resultados, propostas de plano e de skill, opções e limites), load_skill e propose_skill, Prompts/system.md (prompt de sistema proativo embutido, só com o índice das skills)
├── Models/           # Plano, passos, status, resumo de execução, sessão, permissões e linhas das consultas
├── Services/         # Interfaces da UI com o núcleo (planos, Nexus, consultas, sessão, diálogos), ApprovalPolicy, LlmSettingsService (chave da DeepSeek só pelo administrador) e ISkillService (skills da sessão)
│   └── Sqlite/       # Implementações reais: consultas por armazém, planos pelo motor (EnginePlanService), Nexus pelo Planner (PlannerNexusService), skills pelo SkillStore (SqliteSkillService)
├── ViewModels/       # Login, shell e navegação, Nexus (NexusText: destaques das respostas; OptionsViewModel: opções clicáveis), propostas do Nexus com alternativas (DraftPlanViewModel), skill proposta pelo Nexus (SkillProposalViewModel), tela Skills (SkillsViewModel: lista, detalhe e editor com validação ao vivo), Novo plano (PlanForms: formulários gerados do catálogo, listas de escolha, próximo código livre), quadro do plano, resumo visual, telas de consulta (Estoque, Produtos, Zonas, Endereços, Pedidos, Parceiros, Receitas, Auditoria), Planos, Configurações, diálogo
├── Views/            # Login, erro fatal, janela principal, Nexus (texto com destaques em code-behind), proposta do Nexus (Confirmar/Descartar, Por quê, Suposições), skill proposta, tela Skills (lista e detalhe ou editor), opções, Novo plano, quadro do plano, resumo, telas de consulta (incl. Produtos, Zonas, Parceiros), Configurações (provedor de LLM), diálogo de rejeição
│   └── Converters/   # IconKeyConverter (chave de ícone → geometria)
├── tests/
│   └── Nexus.Tests/  # xUnit v3 + Avalonia Headless (Skia real)
│       ├── Core/     # Testes do núcleo por módulo (incl. Skills: documento, store, catálogo e embutidas; Engine, Llm e Planner: PlannerTests, ProductSearchTests, SearchToolsTests, ProposePlanTests, SkillToolsTests, PlannerNexusServiceTests) + TestDatabase e ManualTimeProvider; Data/Fixtures: scripts das versões 1 a 4 do schema (migrações 002 a 005)
│       ├── Services/ # Testes dos serviços Sqlite (consultas, planos, isolamento entre armazéns)
│       ├── Fakes/    # Serviços em memória (antigos serviços de demonstração) e ScriptedLlmProvider (LLM roteirizado; os testes nunca chamam a API real)
│       ├── TestApp.cs  # AppHost real sobre um TestDatabase, com login dos usuários do seed
│       └── *.cs      # Fluxo login/shell/expiração, UI headless (incl. proposta do Nexus), ViewModels, tela Skills e skill proposta (SkillsViewModelTests), Configurações, contraste das cores, política de aprovação
├── ROADMAP.md        # Ordem de implementação das features, da fundação à operação
├── README.md         # Entrega do grupo: problema, canvas, diagramas, UML, partes de IA, Segurança e integração
├── db/               # Banco SQLite: scripts de criação, segurança, carga e desempenho
│   ├── schema.sql    # Tabelas (STRICT), FTS5 de memórias e conhecimento, auditoria
│   ├── security.sql  # Triggers DENY (imutabilidade, anti-escalonamento), permissões efetivas, views v_ai_* e mascaradas
│   ├── seed.sql      # Carga inicial fictícia: papéis, usuários (senha demo "demo" em Argon2id), armazém, estoque, planos, memórias, auditoria com hashes v1
│   ├── volume.sql    # Gera 1 milhão de movimentos sintéticos para o benchmark
│   ├── benchmark.sql # As duas consultas lentas (kardex e FEFO) com EXPLAIN QUERY PLAN e tempo
│   ├── optimize.sql  # Índices que resolvem as consultas lentas
│   ├── migrations/   # Migrações versionadas aplicadas na abertura a bancos antigos (002: decisões de plano, senha demo; 003: catálogo ativo, direitos operacionais da IA, views v_ai_zones/v_ai_lots/v_ai_partners; 004: reservas por pedido; 005: skills versionadas e view v_ai_skills)
│   │   ├── 002_plan_decisions.sql  # Migração 1 → 2: colunas e gatilhos de rejeição de plano, senha demo do seed
│   │   ├── 003_full_catalog.sql    # Migração 2 → 3: is_active em zonas, fornecedores e clientes; permissões da IA; views v_ai_*
│   │   ├── 004_reservations.sql    # Migração 3 → 4: tabela order_reservations (estado guardado por triggers) e preenchimento das reservas do pedido semeado
│   │   └── 005_skills.sql          # Migração 4 → 5: tabelas skills e skill_versions (imutáveis), permissão skill.manage (só supervisor), view v_ai_skills
│   └── tests/
│       └── test_db.py  # Testes: imutabilidade, escalonamento, authorizer da IA, SQL injection, otimização, cadeia de auditoria v1, migrações 2 → 3, 3 → 4 e 4 → 5, reservas, skills
├── tools/            # Utilitários do repositório
│   ├── readme_to_pdf.py  # Converte o README.md (com diagramas Mermaid) em PDF via Chrome headless
│   └── tests/
│       └── test_readme_to_pdf.py  # Testes do conversor (HTML seguro, SRI, detecção do Chrome, PDF gerado)
├── skills/           # Skills embutidas no app (SKILL.md com frontmatter nome/descricao/operacoes, em pt-BR): recebimento, armazenagem, separacao, expedicao, inventario, cadastros
├── knowledge/        # Base do RAG: 22 procedimentos e políticas fictícios de armazém (Markdown)
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
