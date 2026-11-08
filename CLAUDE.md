# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

**Nexus** é um ERP cuja premissa central é ser **totalmente gerenciável por LLMs**: toda operação do sistema deve poder ser descoberta, executada e auditada por um agente de IA, não apenas por uma interface humana.

## Estado atual

A stack está definida em `references/STACK.md` (app desktop nativo em C# com Avalonia, SQLite, instruções de LLM em JSON/Markdown, provedores DeepSeek, Claude e OpenAI). A documentação no código (comentários, XML docs, identificadores, logs) é em inglês. A arquitetura adotada está em `references/ARQUITETURA.md`: o LLM lê dados por tool calling, mas só altera dados enviando um plano JSON (`submit_plan`), que um motor determinístico valida, simula e executa numa transação; planos bem-sucedidos viram receitas reutilizáveis sem LLM. `references/PROPOSTAS_ARQUITETURA.md` guarda as alternativas avaliadas. O banco é só SQLite (SQLCipher, FTS5, `sqlite-vec`); SQL Server, MongoDB e n8n não fazem parte do projeto.

O app C# (Avalonia, MVVM com CommunityToolkit.Mvvm) tem a interface completa e o **núcleo da feature 1** do `ROADMAP.md`. Os módulos do núcleo ficam em `Core/`:
- `Security`: cofre do SO, chaves, Argon2id, AES-GCM.
- `Data`: SQLCipher, as três conexões, o *authorizer* da IA e o bootstrap a partir de `db/*.sql` embutidos.
- `Audit`: cadeia de hash v1.
- `Auth`: login, bloqueio e sessão.
- `Policy`: permissões e escopo por armazém.
- `Engine`: motor de planos (feature 2).
  - DSL v1 (`DslParser`) e JSON Schemas em `Core/Engine/Schemas/v1`.
  - Regras por operação (`*Handler`), `PlanSimulator` (rollback), `PlanExecutor` e `PlanEngine` (máquina de estados).
  - Planos com origem `ai` rodam com os direitos IA ∩ usuário.
- `Llm`: `ILlmProvider` e `DeepSeekProvider` (URL fixa). `LlmSettings` guarda a chave e o modelo no cofre do SO (feature 3).
- `Planner`: laço de tool calling, ferramentas de leitura sobre `v_ai_*`, `submit_plan` (só prévia) e o prompt de sistema em `Core/Planner/Prompts/system.md`.

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
python3 -m unittest discover -s tools/tests -v                     # testes do conversor
dotnet build                                                       # app (sem avisos)
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
