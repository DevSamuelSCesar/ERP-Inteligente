# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Visão geral

ERP Inteligente (nome provisório, projeto ainda sem nome). A premissa central é um ERP **totalmente gerenciável por LLMs**: toda operação do sistema deve poder ser descoberta, executada e auditada por um agente de IA, não apenas por uma interface humana.

## Estado atual

A stack está definida em `references/STACK.md` (app desktop nativo em C# com Avalonia, SQLite, instruções de LLM em JSON/Markdown, provedores DeepSeek, Claude e OpenAI). A documentação no código (comentários, XML docs, identificadores, logs) é em inglês. A arquitetura adotada está em `references/ARQUITETURA.md`: o LLM lê dados por tool calling, mas só altera dados enviando um plano JSON (`submit_plan`), que um motor determinístico valida, simula e executa numa transação; planos bem-sucedidos viram receitas reutilizáveis sem LLM. `references/PROPOSTAS_ARQUITETURA.md` guarda as alternativas avaliadas. Ainda não há código, build ou testes. Quando forem adicionados, documente aqui os comandos de build, lint e testes (incluindo como rodar um único teste).

## `references/`

- `references/` é a especificação e o conceito do projeto. Leia antes de qualquer trabalho; se código e referências divergirem, siga as referências e aponte a divergência.
- `references/TREE.md` mantém a árvore de arquivos do projeto com uma descrição curta de cada item. Atualize-o sempre que arquivos ou diretórios forem criados, removidos, renomeados ou mudarem de propósito.
- Ainda não há documentos de especificação; não os crie sem solicitação.
