# Árvore do projeto

```
erp_inteligente/
├── .gitignore        # Arquivos ignorados pelo git (segredos, bancos .db, PDFs gerados, bin/ e obj/)
├── CLAUDE.md         # Orientações para o Claude Code neste repositório
├── erp_inteligente.csproj  # Projeto do app desktop (Avalonia 12, .NET 10, CommunityToolkit.Mvvm, DataGrid)
├── app.manifest      # Manifesto Windows do executável
├── Program.cs        # Ponto de entrada: plataforma, Barlow como fonte padrão
├── App.axaml(.cs)    # Aplicação: tema escuro, tokens, ícones, estilos; raiz de composição com serviços de demonstração
├── ViewLocator.cs    # Resolve a View de cada ViewModel por convenção de nome
├── Assets/
│   ├── Fonts/        # Família Barlow (SIL OFL, com OFL.txt): Regular, Medium, SemiBold, Condensed, Semi Condensed
│   └── avalonia-logo.ico  # Ícone do template (não usado pela janela)
├── Styles/
│   ├── Tokens.axaml  # Tokens de cor (seção 9 do UI.md), sobrescritas do Fluent e do DataGrid, recursos de fonte
│   ├── Icons.axaml   # Ícones próprios em StreamGeometry (navegação, ações, estados)
│   ├── ButtonTheme.axaml  # ControlTheme próprio do Button (substitui o do Fluent, sem sobreposição translúcida)
│   └── Controls.axaml  # Estilos: botões, navegação, abas, réguas, quadros, DataGrid; sem sombras nem transições
├── Models/           # Plano, passos, status, resumo de execução, sessão, permissões e linhas das consultas
├── Services/         # Interfaces da UI com o motor (planos, Nexus, consultas, sessão, diálogos) e ApprovalPolicy
│   └── Demo/         # Implementações em memória com dados fictícios do seed, até existirem banco e LLM
├── ViewModels/       # Shell e navegação, Nexus, quadro do plano, resumo visual, telas de consulta, Planos, diálogo
├── Views/            # Janela principal, Nexus, quadro do plano, resumo, telas de consulta, diálogo de rejeição
│   └── Converters/   # IconKeyConverter (chave de ícone → geometria)
├── tests/
│   └── Nexus.Tests/  # xUnit v3 + Avalonia Headless: política de aprovação, serviços, ViewModels, contraste, fontes, XAML
├── ROADMAP.md        # Ordem de implementação das features, da fundação à operação
├── README.md         # Entrega do grupo: problema, canvas, diagramas, UML, partes de IA, Segurança e integração
├── db/               # Banco SQLite: scripts de criação, segurança, carga e desempenho
│   ├── schema.sql    # Tabelas (STRICT), FTS5 de memórias e conhecimento, auditoria
│   ├── security.sql  # Triggers DENY (imutabilidade, anti-escalonamento), permissões efetivas, views v_ai_* e mascaradas
│   ├── seed.sql      # Carga inicial fictícia: papéis, usuários, armazém, estoque, planos, memórias
│   ├── volume.sql    # Gera 1 milhão de movimentos sintéticos para o benchmark
│   ├── benchmark.sql # As duas consultas lentas (kardex e FEFO) com EXPLAIN QUERY PLAN e tempo
│   ├── optimize.sql  # Índices que resolvem as consultas lentas
│   └── tests/
│       └── test_db.py  # Testes: imutabilidade, escalonamento, authorizer da IA, SQL injection, otimização
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
