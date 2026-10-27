# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

ERP Inteligente (nome provisório, projeto ainda sem nome). A premissa central é um ERP **totalmente gerenciável por LLMs**: toda operação do sistema deve poder ser descoberta, executada e auditada por um agente de IA, não apenas por uma interface humana.

## Estado atual

A stack está definida em `references/STACK.md` (app desktop nativo em C# com Avalonia, SQLite, instruções de LLM em JSON/Markdown, provedores DeepSeek, Claude e OpenAI). A documentação no código (comentários, XML docs, identificadores, logs) é em inglês. A arquitetura adotada está em `references/ARQUITETURA.md`: o LLM lê dados por tool calling, mas só altera dados enviando um plano JSON (`submit_plan`), que um motor determinístico valida, simula e executa numa transação; planos bem-sucedidos viram receitas reutilizáveis sem LLM. `references/PROPOSTAS_ARQUITETURA.md` guarda as alternativas avaliadas. O banco é só SQLite (SQLCipher, FTS5, `sqlite-vec`); SQL Server, MongoDB e n8n não fazem parte do projeto.

O app C# ainda não existe. Já existem os scripts SQLite em `db/` (aplicar na ordem `schema.sql` → `security.sql` → `seed.sql`; `volume.sql`, `benchmark.sql` e `optimize.sql` são da medição de desempenho) e os testes Python que os validam. `db/tests/test_db.py` também contém o *authorizer* de referência da conexão da IA, que o app C# deve reproduzir. Os dados do repositório são sempre fictícios.

## Comandos

```bash
python3 -m unittest discover -s db/tests -v                       # todos os testes de banco
python3 -m unittest db.tests.test_db.SqlInjectionTests -v          # uma classe (rodar da raiz)
python3 -m unittest db.tests.test_db.AiConnectionTests.test_ai_cannot_write
sqlite3 /tmp/erp.db < db/schema.sql && sqlite3 /tmp/erp.db < db/security.sql && sqlite3 /tmp/erp.db < db/seed.sql
python3 tools/readme_to_pdf.py -o /tmp/README.pdf                  # README → PDF (Chrome/Chromium headless)
python3 -m unittest discover -s tools/tests -v                     # testes do conversor
```

PDFs são ignorados pelo git (`*.pdf`); nunca os adicione ao repositório. Os diagramas Mermaid do `README.md` são cópias dos de `references/DIAGRAMAS.md` (seções 1 e 2) e `references/UML.md`; ao alterar um, altere o outro.

Quando o projeto C# for criado, documente aqui build, lint e testes.

## Documento de entrega

O `README.md` da raiz é a entrega acadêmica do grupo ERP INTELIGENTE (disciplinas de IA e de Segurança/Otimização de BD) e resume `references/`. Ao mudar arquitetura, segurança, custos ou números de benchmark, atualize também o `README.md`, `references/IA.md` e `references/SEGURANCA_DADOS.md`.

## `references/`

- `references/` é a especificação e o conceito do projeto. Leia antes de qualquer trabalho; se código e referências divergirem, siga as referências e aponte a divergência.
- `references/TREE.md` mantém a árvore de arquivos do projeto com uma descrição curta de cada item. Atualize-o sempre que arquivos ou diretórios forem criados, removidos, renomeados ou mudarem de propósito.
- `references/DIAGRAMAS.md` traz os diagramas Mermaid da arquitetura; mantenha-o coerente com `ARQUITETURA.md`.
- Não crie novos documentos de especificação sem solicitação.
