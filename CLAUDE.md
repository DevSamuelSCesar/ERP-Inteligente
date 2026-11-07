# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

**Nexus** é um ERP cuja premissa central é ser **totalmente gerenciável por LLMs**: toda operação do sistema deve poder ser descoberta, executada e auditada por um agente de IA, não apenas por uma interface humana.

## Estado atual

A stack está definida em `references/STACK.md` (app desktop nativo em C# com Avalonia, SQLite, instruções de LLM em JSON/Markdown, provedores DeepSeek, Claude e OpenAI). A documentação no código (comentários, XML docs, identificadores, logs) é em inglês. A arquitetura adotada está em `references/ARQUITETURA.md`: o LLM lê dados por tool calling, mas só altera dados enviando um plano JSON (`submit_plan`), que um motor determinístico valida, simula e executa numa transação; planos bem-sucedidos viram receitas reutilizáveis sem LLM. `references/PROPOSTAS_ARQUITETURA.md` guarda as alternativas avaliadas. O banco é só SQLite (SQLCipher, FTS5, `sqlite-vec`); SQL Server, MongoDB e n8n não fazem parte do projeto.

O app C# existe só como **interface** (Avalonia, MVVM com CommunityToolkit.Mvvm): shell com coluna de menus, Nexus, quadro do plano, resumo visual e telas de consulta. Por trás dela há serviços em memória com dados fictícios (`Services/Demo/`) que substituem o banco e o LLM até as features 1–3 do `ROADMAP.md`; as interfaces em `Services/` são o ponto de troca. Já existem os scripts SQLite em `db/` (aplicar na ordem `schema.sql` → `security.sql` → `seed.sql`; `volume.sql`, `benchmark.sql` e `optimize.sql` são da medição de desempenho) e os testes Python que os validam. `db/tests/test_db.py` também contém o *authorizer* de referência da conexão da IA, que o app C# deve reproduzir. Os dados do repositório são sempre fictícios.

## Comandos

```bash
python3 -m unittest discover -s db/tests -v                       # todos os testes de banco
python3 -m unittest db.tests.test_db.SqlInjectionTests -v          # uma classe (rodar da raiz)
python3 -m unittest db.tests.test_db.AiConnectionTests.test_ai_cannot_write
sqlite3 /tmp/erp.db < db/schema.sql && sqlite3 /tmp/erp.db < db/security.sql && sqlite3 /tmp/erp.db < db/seed.sql
python3 tools/readme_to_pdf.py -o /tmp/README.pdf                  # README → PDF (Chrome/Chromium headless)
python3 -m unittest discover -s tools/tests -v                     # testes do conversor
dotnet build                                                       # app (sem avisos)
dotnet run                                                         # abre o app com dados fictícios
dotnet test tests/Nexus.Tests                                      # testes do app: ViewModels, serviços, contraste, UI headless
dotnet test tests/Nexus.Tests --filter "FullyQualifiedName~PlanBoard"  # filtro por nome
```

PDFs são ignorados pelo git (`*.pdf`); nunca os adicione ao repositório. Os diagramas Mermaid do `README.md` são cópias dos de `references/DIAGRAMAS.md` (seções 1 e 2) e `references/UML.md`; ao alterar um, altere o outro.

Os testes do app usam xUnit v3 + `Avalonia.Headless.XUnit` com Skia real, então carregam o XAML, os estilos e as fontes de verdade. `PaletteContrastTests` lê `Styles/Tokens.axaml` e falha se um par de cores sair da tabela WCAG do `UI.md`; ao mudar uma cor, atualize os dois.

## Documento de entrega

O `README.md` da raiz é a entrega acadêmica do grupo ERP INTELIGENTE (disciplinas de IA e de Segurança/Otimização de BD) e resume `references/`. Ao mudar arquitetura, segurança, custos ou números de benchmark, atualize também o `README.md`, `references/IA.md` e `references/SEGURANCA_DADOS.md`.

## `references/`

- `references/` é a especificação e o conceito do projeto. Leia antes de qualquer trabalho; se código e referências divergirem, siga as referências e aponte a divergência.
- `references/TREE.md` mantém a árvore de arquivos do projeto com uma descrição curta de cada item. Atualize-o sempre que arquivos ou diretórios forem criados, removidos, renomeados ou mudarem de propósito.
- `references/DIAGRAMAS.md` traz os diagramas Mermaid da arquitetura; mantenha-o coerente com `ARQUITETURA.md`.
- `references/UI.md` define layout e identidade visual; `references/NEGATIVAS_WEB.md` lista o que não fazer (lógica web, clichês de SaaS e de LLM). Toda tela nova passa pela checklist de `NEGATIVAS_WEB.md`.
- Não crie novos documentos de especificação sem solicitação.
