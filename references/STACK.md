# Stack

## Aplicação

- **Nativa, não web.** O ERP é um aplicativo desktop nativo; não deve ser construído como aplicação web nem depender de um navegador para rodar.
- **Linguagem:** C#.
- **Interface:** Avalonia.

## Dados

- **Dados internos:** SQLite.
- **Instruções para LLMs:** arquivos JSON e Markdown (`.md`).

## Provedores de LLM

O ERP deve oferecer suporte às APIs dos seguintes provedores:

- DeepSeek
- Claude (Anthropic)
- OpenAI

## Convenções

- **Documentação no código em inglês:** comentários, XML doc comments (`///`), nomes de identificadores, mensagens de log e READMEs de código.
- Os documentos de `references/` continuam em português.
