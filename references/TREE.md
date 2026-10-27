# Árvore do projeto

```
erp_inteligente/
├── .gitignore        # Arquivos ignorados pelo git (segredos, bancos .db, builds)
├── CLAUDE.md         # Orientações para o Claude Code neste repositório
├── README.md         # Entrega do grupo: problema, canvas, partes de IA, Segurança e integração
├── db/               # Banco SQLite: scripts de criação, segurança, carga e desempenho
│   ├── schema.sql    # Tabelas (STRICT), FTS5 de memórias e conhecimento, auditoria
│   ├── security.sql  # Triggers DENY (imutabilidade, anti-escalonamento), permissões efetivas, views v_ai_* e mascaradas
│   ├── seed.sql      # Carga inicial fictícia: papéis, usuários, armazém, estoque, planos, memórias
│   ├── volume.sql    # Gera 1 milhão de movimentos sintéticos para o benchmark
│   ├── benchmark.sql # As duas consultas lentas (kardex e FEFO) com EXPLAIN QUERY PLAN e tempo
│   ├── optimize.sql  # Índices que resolvem as consultas lentas
│   └── tests/
│       └── test_db.py  # Testes: imutabilidade, escalonamento, authorizer da IA, SQL injection, otimização
├── knowledge/        # Base do RAG: 22 procedimentos e políticas fictícios de armazém (Markdown)
│   └── README.md     # Origem e organização dos documentos
└── references/       # Especificação e conceito do projeto
    ├── ARQUITETURA.md            # Arquitetura adotada: LLM planeja em JSON, motor determinístico executa; segurança de dados
    ├── CANVAS.md                 # Canvas de uma página: problema, entrada/saída, arquitetura em caixas, risco, MVP
    ├── DIAGRAMAS.md              # Diagramas Mermaid: arquitetura integrada, ataques, fluxo, ciclos de vida, dados
    ├── IA.md                     # LLMs escolhidos, custo por 1.000 usos, RAG, fine-tuning, riscos e métricas
    ├── PROPOSTAS_ARQUITETURA.md  # Alternativas de arquitetura avaliadas antes da decisão (histórico)
    ├── SEGURANCA_DADOS.md        # Classificação de dados, matriz de papéis, ameaças, otimização, ataques testados
    ├── STACK.md                  # Stack: C#/Avalonia nativo, SQLite + SQLCipher, instruções JSON/MD, provedores de LLM
    ├── TREE.md                   # Esta árvore de arquivos com descrições
    └── UML.md                    # UML: casos de uso, classes do domínio e do motor, componentes e implantação
```
