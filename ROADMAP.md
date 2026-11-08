# Roadmap

Ordem de implementação das features do Nexus, derivada de [`references/ARQUITETURA.md`](references/ARQUITETURA.md). Cada feature só começa quando a anterior está testada. Sistemas interligados entram juntos na mesma feature.

> Já pronto: schema, segurança, seed e testes do SQLite em `db/`, e a base de conhecimento em `knowledge/`.

## Fundação

### 1. Núcleo da aplicação e acesso ao banco ✅
Solução C#, app Avalonia (MVVM) com login e sessão, SQLCipher com chaves no cofre do SO, as três conexões (IA somente leitura com *authorizer*, Executor, Aplicação), policy engine (permissão + escopo por armazém) e auditoria encadeada por hash com verificação na inicialização.
*Por que junto:* login, permissões, conexões e auditoria são a base de segurança de tudo que vem depois.
*Estado:* pronto. Código em `Core/`, telas lendo o banco real por `Services/Sqlite/`. O cofre do macOS (Keychain) ainda não é suportado.

### 2. Motor determinístico de planos ✅
DSL JSON versionada, Validator (JSON Schema + regras de negócio), Simulator (diff e risco), Approval (segregação de funções) e Executor transacional. Operações iniciais: `receive_goods`, `move_stock`, `create_product`, `create_location`. Operável sem LLM, por testes e por uma tela de pré-visualização do plano.
*Por que junto:* Validator, Simulator, Approval e Executor formam um pipeline único e o catálogo de operações define os três.
*Estado:* pronto.
- Código em `Core/Engine`; tela "Novo plano"; aprovação e rejeição em Planos.
- `adjust_stock` foi antecipado da feature 4, para a aprovação funcionar de ponta a ponta.
- O banco ganhou a migração versionada 1 → 2 (`db/migrations/`).

## Camada de IA

### 3. Planner com tool calling ✅
`ILlmProvider` com o primeiro adaptador, ferramentas de leitura sobre as views `v_ai_*`, `propose_plan` com devolução de erros estruturados e limite de tentativas, e tela de chat com prévia do plano e confirmação.
*Por que junto:* o Planner só tem valor com as ferramentas de leitura e a tela de chat que o alimentam e exibem seu resultado.
*Estado:* pronto, só com a **DeepSeek**. Claude e OpenAI ficam para a feature 8. Redesenhado em 2026-11-08 para o modo proativo: 8 ferramentas de leitura e pesquisa, `propose_plan` com até 3 alternativas, `offer_options` com botões de próximo passo, e formulários do Novo plano gerados do catálogo.
- Código em `Core/Llm` (provedor, chave no cofre do SO) e `Core/Planner` (laço de ferramentas, prompt de sistema).
- A chave é digitada pelo administrador em Configurações.
- O plano da IA volta como proposta: só é gravado quando quem pediu confirma.
- O motor aplica os direitos IA ∩ usuário a todo plano da IA.

### 4. Skills
Carregador de skills (`SKILL.md` + `skill.json`) com índice no prompt e ferramentas filtradas por skill. Skills `recebimento` e `armazenagem` primeiro; depois `separacao`, `expedicao`, `inventario` e `cadastros`.
*Por que junto:* cada skill depende das operações que ela permite.
*Estado:* parcial. O catálogo completo de operações já está pronto (20 operações, incluindo `reserve_stock`, `pick_items`, `ship_order` e os cancelamentos), antecipado para 2026-11-08. Falta o carregador de skills e as skills.

### 5. Memória e RAG
Tabela `memories` com filtro de proteção, busca FTS5 por escopo e confiança; indexação de `knowledge/` com embeddings e `sqlite-vec`, busca híbrida (BM25 + vetor + RRF) e injeção dos trechos como dados delimitados.
*Por que junto:* os dois são recuperação de texto livre no mesmo contexto do Planner e compartilham índices e delimitação anti-injection.

## Segurança e economia

### 6. Defesas de prompt injection e suíte de ataques
Delimitação de conteúdo externo no prompt, relato de instruções suspeitas, filtro de logs sensíveis e a suíte automatizada de prompt injection e SQL injection com métricas.
*Entra antes das receitas para que o que for promovido já nasça validado contra ataques.*

### 7. Receitas
Promoção de planos repetidos a receitas parametrizadas, Recipe Matcher com modelo barato, disparo direto pela UI sem LLM, invalidação por versão da DSL/skill e despromoção por falhas.

## Evolução

### 8. Multi-provedor e fine-tuning
Adaptadores Claude e OpenAI (a DeepSeek entrou na feature 3), modelo por papel configurável e experimento de fine-tuning do Matcher, que só substitui o Haiku se empatar ou superar no *exact match*.

### 9. Operação
Backup criptografado pela API do SQLite, job de manutenção de memórias, painel de auditoria e de custos de tokens, e instaladores por plataforma.

## Resumo

```
1 Núcleo + acesso → 2 Motor de planos → 3 Planner + chat → 4 Skills
   → 5 Memória + RAG → 6 Defesas e ataques → 7 Receitas → 8 Multi-provedor + fine-tuning → 9 Operação
```

O MVP do [`CANVAS.md`](references/CANVAS.md) corresponde às features 1 a 3, às skills `recebimento` e `armazenagem` da 4, ao RAG da 5 e ao núcleo da 6. Receitas, fine-tuning, inventário e expedição ficam fora dele.
