# Stack

## Aplicação

- **Nativa, não web.** O ERP é um aplicativo desktop nativo; não deve ser construído como aplicação web nem depender de um navegador para rodar.
- **Linguagem:** C#.
- **Interface:** Avalonia.

## Dados

- **Dados internos:** SQLite, criptografado em repouso com **SQLCipher**.
- **Busca:** FTS5 (texto) e **`sqlite-vec`** (vetores do RAG), no mesmo arquivo SQLite.
- **Instruções para LLMs:** arquivos JSON e Markdown (`.md`).
- **Não usados:** SQL Server, MongoDB e n8n não fazem parte do projeto.

## Provedores de LLM

O ERP deve oferecer suporte às APIs dos seguintes provedores:

- DeepSeek
- Claude (Anthropic)
- OpenAI

Embeddings do RAG: `text-embedding-3-small` (OpenAI) ou um modelo de embeddings local. Modelos padrão e custos em [`IA.md`](IA.md).

## Testes dos scripts de banco

Python 3 (biblioteca padrão `sqlite3` + `unittest`) testa os scripts de `db/` enquanto a aplicação C# não existe.

## Convenções

- **Documentação no código em inglês:** comentários, XML doc comments (`///`), nomes de identificadores, mensagens de log e READMEs de código.
- Os documentos de `references/` continuam em português.
