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
├── Core/             # Núcleo da feature 1 do ROADMAP (seção 12 da ARQUITETURA)
│   ├── Security/     # Cofre do SO (libsecret, DPAPI, memória), KeyManager, DatabaseKeys, ColumnCipher (AES-GCM), PasswordHasher (Argon2id)
│   ├── Data/         # NexusDatabase (3 conexões SQLCipher), AiAuthorizer, DatabaseBootstrapper (cria o banco a partir de db/*.sql)
│   ├── Audit/        # AuditEntry, AuditHasher (regra v1), AuditLog (append encadeado), AuditVerifier
│   ├── Auth/         # AuthService (login, bloqueio, auditoria), SessionService (expiração por inatividade), LoginResult
│   └── Policy/       # PolicyEngine (deny-overrides, interseção IA ∩ usuário), ApplicationDb (consultas com permissão e $warehouse_id)
├── Models/           # Plano, passos, status, resumo de execução, sessão, permissões e linhas das consultas
├── Services/         # Interfaces da UI com o núcleo (planos, Nexus, consultas, sessão, diálogos) e ApprovalPolicy
│   └── Sqlite/       # Implementações reais: consultas por armazém, planos somente leitura, Nexus só de consulta (conexão da IA)
├── ViewModels/       # Login, shell e navegação, Nexus, quadro do plano, resumo visual, telas de consulta, Planos, diálogo
├── Views/            # Login, erro fatal, janela principal, Nexus, quadro do plano, resumo, telas de consulta, diálogo de rejeição
│   └── Converters/   # IconKeyConverter (chave de ícone → geometria)
├── tests/
│   └── Nexus.Tests/  # xUnit v3 + Avalonia Headless (Skia real)
│       ├── Core/     # Testes do núcleo por módulo + TestDatabase (banco SQLCipher temporário) e ManualTimeProvider
│       ├── Services/ # Testes dos serviços Sqlite (consultas, planos, Nexus, isolamento entre armazéns)
│       ├── Fakes/    # Serviços em memória (antigos serviços de demonstração) para testar ViewModels do quadro de aprovação
│       ├── TestApp.cs  # AppHost real sobre um TestDatabase, com login dos usuários do seed
│       └── *.cs      # Fluxo login/shell/expiração, UI headless, ViewModels, contraste das cores, política de aprovação
├── ROADMAP.md        # Ordem de implementação das features, da fundação à operação
├── README.md         # Entrega do grupo: problema, canvas, diagramas, UML, partes de IA, Segurança e integração
├── db/               # Banco SQLite: scripts de criação, segurança, carga e desempenho
│   ├── schema.sql    # Tabelas (STRICT), FTS5 de memórias e conhecimento, auditoria
│   ├── security.sql  # Triggers DENY (imutabilidade, anti-escalonamento), permissões efetivas, views v_ai_* e mascaradas
│   ├── seed.sql      # Carga inicial fictícia: papéis, usuários (senha demo em Argon2id), armazém, estoque, planos, memórias, auditoria com hashes v1
│   ├── volume.sql    # Gera 1 milhão de movimentos sintéticos para o benchmark
│   ├── benchmark.sql # As duas consultas lentas (kardex e FEFO) com EXPLAIN QUERY PLAN e tempo
│   ├── optimize.sql  # Índices que resolvem as consultas lentas
│   └── tests/
│       └── test_db.py  # Testes: imutabilidade, escalonamento, authorizer da IA, SQL injection, otimização, cadeia de auditoria v1
├── tools/            # Utilitários do repositório
│   ├── readme_to_pdf.py  # Converte o README.md (com diagramas Mermaid) em PDF via Chrome headless
│   └── tests/
│       └── test_readme_to_pdf.py  # Testes do conversor (HTML seguro, SRI, detecção do Chrome, PDF gerado)
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
