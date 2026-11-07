# Arquitetura

> Escopo atual: módulo de **armazém (WMS)**. Stack definida em [`STACK.md`](STACK.md).

## 1. Princípio central

**O LLM descreve as alterações; o código as executa.**

O LLM usa tool calling o tempo todo, mas de duas formas diferentes:

- **Ferramentas de leitura** (`get_stock`, `find_location`, `search_recipes`...) executam na hora e devolvem dados ao LLM.
- **Alterações de dados** nunca acontecem por chamadas individuais. O LLM monta um **plano JSON** com todos os passos e o envia por uma única ferramenta, `submit_plan`. Um motor determinístico, sem LLM, valida o plano, simula o efeito e o executa numa única transação: aplica tudo ou nada.

Assim, a consistência dos dados é garantida pelo código (validação + transação) e não depende do raciocínio do modelo durante a execução.

---

## 2. Premissas núcleo

| # | Premissa | Como a arquitetura atende |
|---|----------|---------------------------|
| P1 | Pouca intervenção manual do operador | O LLM planeja de ponta a ponta. O humano só confirma planos de risco crítico. |
| P2 | Pouca necessidade de conhecimento técnico | O operador conversa em linguagem natural e vê uma pré-visualização do plano em termos do negócio. |
| P3 | Automação confiável via tool calling | Ferramentas tipadas para leitura; escrita só por planos validados e transacionais. |
| P4 | Sistema de skills do ERP | Os procedimentos do armazém ficam em skills (Markdown + JSON) carregadas sob demanda. |
| P5 | Eficiência de tokens | Contexto mínimo por tarefa, ferramentas filtradas pela skill e **receitas** que rodam sem LLM. |
| P6 | RAG quando houver vantagem | Só sobre texto livre: memórias, skills e receitas. Dados estruturados são lidos por consultas tipadas. |
| P7 | Melhoria contínua | Memórias e receitas são gravadas em SQLite e consultadas nas próximas tarefas. |

---

## 3. Visão geral

```
┌──────────────────────────────────────────────────────────────┐
│ UI (Avalonia, MVVM)                                          │
│  chat · pré-visualização do plano · confirmação · consultas  │
└───────────────┬──────────────────────────────────────────────┘
                │ pedido em linguagem natural
┌───────────────▼──────────────────────────────────────────────┐
│ Recipe Matcher ── existe receita para este pedido?           │
│   sim → LLM barato só preenche parâmetros (ou nenhum LLM)    │
│   não → Planner                                              │
└───────────────┬──────────────────────────────────────────────┘
┌───────────────▼──────────────────────────────────────────────┐
│ Planner (LLM)                                                │
│  skill ativa · memórias relevantes · ferramentas de leitura  │
│  → submit_plan(plano JSON)                                   │
└───────────────┬──────────────────────────────────────────────┘
                │ a partir daqui, sem LLM
┌───────────────▼──────────────────────────────────────────────┐
│ Validator ── JSON Schema + regras de negócio                 │
│   inválido → erro estruturado devolvido ao Planner           │
├──────────────────────────────────────────────────────────────┤
│ Simulator (dry-run) ── calcula o diff de estoque e o risco   │
├──────────────────────────────────────────────────────────────┤
│ Approval ── risco crítico → confirmação do operador          │
├──────────────────────────────────────────────────────────────┤
│ Executor ── transação SQLite (tudo ou nada)                  │
└───────────────┬──────────────────────────────────────────────┘
┌───────────────▼──────────────────────────────────────────────┐
│ Learning ── grava memórias · promove planos a receitas       │
└──────────────────────────────────────────────────────────────┘
        Persistência: SQLite (domínio, planos, receitas, memórias, auditoria)
```

---

## 4. Fluxo de uma tarefa

1. **Pedido:** o operador escreve, por exemplo, "Guarde as 40 caixas do ABC-123 que estão na doca".
2. **Receita:** o Recipe Matcher procura uma receita compatível (seção 7). Se encontrar, pula para o passo 6 com o plano gerado a partir dela.
3. **Skill:** o Planner escolhe a skill pelo índice (nome + descrição) e só então carrega o `SKILL.md` completo.
4. **Contexto:** entram no contexto as memórias relevantes (top-k pequeno, filtrado por escopo) e as ferramentas de leitura permitidas pela skill.
5. **Planejamento:** o LLM consulta o estado com ferramentas de leitura e chama `submit_plan`.
6. **Validação:** se o plano for inválido, o erro volta ao LLM em formato estruturado para ser corrigido, com limite de tentativas. Ao atingir o limite, a tarefa é escalada ao operador.
7. **Simulação:** o dry-run calcula o efeito (o que entra, o que sai e o que muda de endereço) e o nível de risco do plano.
8. **Aprovação:** um plano de risco crítico é mostrado ao operador em linguagem de negócio para confirmação. Os demais seguem direto.
9. **Execução:** o Executor aplica todos os passos numa transação. Se qualquer passo falhar, nada é gravado.
10. **Aprendizado:** o resultado é registrado; memórias e receitas são atualizadas (seções 7 e 8).

### Exemplo

```
LLM → find_location({ sku: "ABC-123", qty: 40 })      ← leitura, executa na hora
    ← [{ location: "A-03-02", free: 20 }, { location: "A-03-04", free: 30 }]
LLM → submit_plan({ ...plano... })
    ← { status: "executed", plan_id: 812, summary: "40 cx ABC-123: DOCA-01 → A-03-02 (20), A-03-04 (20)" }
```

---

## 5. Plano JSON (DSL)

```json
{
  "intent": "putaway",
  "skill": "armazenagem",
  "steps": [
    { "op": "move_stock", "args": { "sku": "ABC-123", "qty": 20, "from": "DOCA-01", "to": "A-03-02" } },
    { "op": "move_stock", "args": { "sku": "ABC-123", "qty": 20, "from": "DOCA-01", "to": "A-03-04" } }
  ]
}
```

Regras da DSL:

- `op` pertence a um catálogo fechado de **operações de escrita**. Cada operação tem um schema de argumentos, regras de validação e um nível de risco.
- Um passo pode guardar seu resultado com `"as": "nome"`, e passos seguintes podem referenciá-lo com `"$nome"`.
- Não há laços nem condicionais arbitrárias. O que exigir lógica complexa vira uma nova operação no catálogo, implementada em C# e testada.
- A DSL é **versionada**. Planos e receitas guardam a versão com que foram criados.

### Catálogo inicial de operações (armazém)

| Operação | Descrição | Risco |
|----------|-----------|-------|
| `receive_goods` | Registra a entrada de itens na doca | Reversível |
| `move_stock` | Transfere saldo entre endereços | Reversível |
| `reserve_stock` | Reserva saldo para um pedido de saída | Reversível |
| `pick_items` | Registra a separação de itens | Reversível |
| `ship_order` | Expede e baixa o estoque | Crítico |
| `adjust_stock` | Ajusta saldo por inventário ou avaria (exige motivo) | Crítico |
| `create_location` / `create_product` | Cadastros | Reversível |

O risco do plano é o **maior risco** entre seus passos.

---

## 6. Ferramentas expostas ao LLM

| Ferramenta | Tipo | Função |
|------------|------|--------|
| Consultas de domínio (`get_stock`, `find_location`, `get_order`, `list_pending_receipts`...) | Leitura | Respostas compactas e paginadas |
| `search_memories` | Leitura | Busca memórias além das já injetadas no contexto |
| `search_recipes` | Leitura | Busca receitas similares para usar como base |
| `load_skill` | Leitura | Carrega o `SKILL.md` de uma skill |
| `submit_plan` | Escrita (via motor) | Única porta de entrada para alterar dados |
| `remember` | Memória | Registra uma memória candidata (seção 8) |

A skill ativa define quais consultas e quais operações do catálogo ficam disponíveis.

---

## 7. Receitas

Uma **receita** é um plano parametrizado que já funcionou. É o principal mecanismo de economia de tokens: com o uso, cada vez mais tarefas rodam sem planejamento pelo LLM.

- **Promoção:** um plano é candidato a receita quando a mesma estrutura de passos é executada com sucesso repetidas vezes, sem correção do operador. Os valores variáveis (SKU, quantidade, endereço) viram parâmetros.
- **Uso:**
  - Disparo direto pela UI (botão ou atalho) com parâmetros preenchidos pelo operador: **nenhum LLM**.
  - Disparo por linguagem natural: um modelo barato só identifica a receita e extrai os parâmetros.
- **Validação:** o plano gerado a partir de uma receita passa pelo mesmo Validator, Simulator e Approval de qualquer plano.
- **Invalidação:** a receita guarda a versão da DSL e da skill. Se uma das duas mudar, a receita fica suspensa até ser revalidada. Receitas que falham repetidamente são despromovidas.

---

## 8. Memória e melhoria contínua

Tabela `memories` no SQLite:

| Campo | Descrição |
|-------|-----------|
| `kind` | `correction`, `preference`, `pattern`, `fact` |
| `scope` | `global`, `skill`, `sku`, `location`, `supplier`... e o identificador do escopo |
| `content` | Texto curto e objetivo |
| `confidence` | Aumenta quando a memória é útil e diminui quando é contrariada |
| `use_count`, `last_used_at` | Uso e recência, para ranking e descarte |
| `source_plan_id` | Plano ou tarefa que originou a memória |

- **Origem:** correções do operador, rejeições na aprovação, erros de validação recorrentes e o resultado das execuções.
- **Busca:** FTS5 com ranking BM25, combinado com filtro por escopo, confiança e recência.
- **Manutenção:** um job periódico deduplica, mescla memórias parecidas e descarta as de baixa confiança ou sem uso.
- **Proteção:** memórias passam por um filtro antes de serem gravadas: tamanho máximo de 1.000 caracteres, sem dados sensíveis (CPF, e-mail, telefone, CNPJ completo) e sem texto que pareça instrução ao modelo. Memórias criadas a partir de conteúdo externo entram com confiança baixa.

### RAG

| Etapa | Escolha |
|-------|---------|
| Fontes | Procedimentos e políticas do armazém (`knowledge/*.md`), `SKILL.md` das skills e descrições de receitas |
| Chunking | Um trecho por seção `##` do Markdown, com o título do documento e da seção no início do trecho; seções acima de ~500 tokens são divididas com sobreposição de ~50 tokens |
| Embeddings | `text-embedding-3-small` (OpenAI, 1.536 dimensões). Anthropic e DeepSeek não oferecem API de embeddings. Alternativa local, sem enviar texto para fora: um modelo de embeddings aberto rodando na própria máquina |
| Banco vetorial | `sqlite-vec` (tabela virtual `vec0`) no mesmo arquivo SQLite, ligada a `knowledge_chunks` |
| Busca | Híbrida: top-20 do FTS5/BM25 + top-20 por similaridade de cosseno, combinados por *Reciprocal Rank Fusion*; os 4 melhores trechos entram no contexto |
| Atualização | Cada trecho guarda o SHA-256 do conteúdo; só trechos alterados geram embedding de novo |

Os trechos recuperados entram no prompt **delimitados como dados de referência**, com o caminho do documento de origem, e nunca como instruções (seção 12.8).

### Política de RAG

| Usar RAG | Não usar RAG |
|----------|--------------|
| Procedimentos, políticas, skills, memórias, receitas | Saldos, endereços, pedidos e qualquer dado estruturado: são lidos por consultas tipadas |

---

## 9. Skills

```
skills/
└── armazenagem/
    ├── SKILL.md     # Instruções padrão: passos, regras do negócio, exemplos curtos de planos
    └── skill.json   # Nome, descrição, gatilhos, consultas e operações permitidas, versão
```

- O prompt de sistema leva só o índice das skills. O conteúdo completo entra apenas quando a skill é ativada.
- O `SKILL.md` traz exemplos de planos válidos, o que reduz tentativas de correção.
- Skills iniciais: `recebimento`, `armazenagem`, `separacao`, `expedicao`, `inventario`, `cadastros`.

---

## 10. Provedores de LLM

- Interface única `ILlmProvider` (chat, tool calling, streaming e contagem de tokens) com adaptadores para **Claude**, **OpenAI** e **DeepSeek**. A API da DeepSeek é compatível com a da OpenAI.
- **Modelo por papel:** um modelo barato para identificar receitas e extrair parâmetros, e um modelo forte para planejar e tratar exceções. Ambos são configuráveis.

| Papel | Modelo padrão | Alternativas suportadas |
|-------|---------------|-------------------------|
| Planner | Claude Sonnet 5.5 (`claude-sonnet-5-5`) | Claude Opus 5.5, `deepseek-v4-pro`, modelos da OpenAI |
| Receitas e extração de parâmetros | Claude Haiku 5.5 (`claude-haiku-5-5`) ou o modelo ajustado por fine-tuning (seção 10.1) | `deepseek-flash` |
| Embeddings | `text-embedding-3-small` | Modelo de embeddings local |

A justificativa e o custo por 1.000 usos estão em [`IA.md`](IA.md).

- As chaves de API ficam no armazenamento seguro do sistema operacional, nunca no SQLite nem no repositório.

### 10.1 Fine-tuning

Experimento para reduzir custo e dependência de rede na etapa mais frequente: transformar um pedido em uma receita com parâmetros.

| Item | Definição |
|------|-----------|
| Tarefa | Pedido em linguagem natural → JSON `{intent, recipe, params}` (ou `{"intent":"none"}` quando nenhuma receita serve) |
| Dados | ~500 pedidos fictícios gerados a partir das receitas e do catálogo de teste, com variações de escrita, erros de digitação e pedidos fora do escopo; anotados e revisados pelo grupo; divisão 80/20 |
| Modelo base | Modelo aberto pequeno (classe 1–3 bilhões de parâmetros, ex.: Qwen2.5-1.5B-Instruct) com LoRA, rodando localmente |
| Métricas | *Exact match* do JSON, F1 por campo de parâmetro, taxa de JSON válido e taxa de acerto em "nenhuma receita" |
| Comparação | Mesmo conjunto de teste com Claude Haiku 5.5 em *few-shot*; o modelo ajustado só substitui o Haiku se empatar ou superar no *exact match* |

---

## 11. Persistência (SQLite)

| Grupo | Conteúdo |
|-------|----------|
| Domínio | Produtos, endereços, saldos, lotes, fornecedores, clientes, pedidos de entrada e saída |
| Livro-razão | `stock_movements`: uma linha por mudança de estoque, nunca alterada nem apagada |
| Planos | Plano JSON, status (`validating`, `pending_approval`, `rejected`, `executed`, `failed`), diff simulado, tentativas, aprovador |
| Receitas | Plano parametrizado, parâmetros, versões da DSL e da skill, estatísticas de uso e falha |
| Memórias | Tabela `memories` + índice FTS5 |
| Conhecimento (RAG) | `knowledge_chunks` + índice FTS5 + vetores `sqlite-vec` |
| Acesso | `roles`, `role_permissions` (ALLOW/DENY), `users` |
| Auditoria | `audit_log` encadeado por hash |

O schema está em `db/schema.sql` e as regras de banco em `db/security.sql`. Toda execução de plano roda numa única transação SQLite.

---

## 12. Segurança de dados

### 12.1 Princípios

- **Defesa em profundidade:** cada regra importante é aplicada em pelo menos duas camadas (aplicação e banco).
- **Menor privilégio:** cada componente recebe só o acesso de que precisa. A IA tem o menor acesso de todos.
- **A IA é um usuário não confiável:** tudo o que vem do LLM (planos, argumentos, memórias) é tratado como entrada externa e validado como se viesse de um formulário.
- **Nada some:** movimentos, planos e auditoria são append-only. Correções são feitas por novos registros.

### 12.2 Classificação dos dados

| Classe | Exemplos | Regra |
|--------|----------|-------|
| Público | SKU, nome e descrição de produto, código de endereço, glossário | Pode aparecer em qualquer tela e no contexto da IA |
| Interno | Saldos, lotes, pedidos, nome de fornecedor e cliente, planos, memórias | Só usuários autenticados com permissão; a IA vê pelas views `v_ai_*` |
| Sensível | CPF, nome completo, e-mail e telefone de pessoas, CNPJ/CPF completo de terceiros, endereço de entrega, custo unitário, hash de senha | Criptografado por coluna, exibido mascarado, nunca enviado ao LLM, leitura só com `sensitive.read` |

A classificação coluna por coluna está em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md).

### 12.3 Proteção em repouso

1. **Banco inteiro criptografado com SQLCipher** (AES-256). A chave é aleatória, gerada na instalação e guardada no cofre do sistema operacional: DPAPI no Windows, Keychain no macOS, Secret Service (libsecret) no Linux. Sem a chave, o arquivo `.db` copiado é ilegível.
   - São duas chaves aleatórias de 32 bytes por banco: a do SQLCipher e a das colunas. No cofre, cada uma é identificada pelo caminho absoluto do arquivo `.db` e pelo nome (`sqlcipher` ou `column`). No Linux usa o schema libsecret `app.nexus.erp`, com os atributos `db` e `key`.
   - O SQLCipher abre com a chave bruta (`PRAGMA key = "x'<hex>'"`), sem derivação por senha.
   - Se o arquivo `.db` existe e a chave não está no cofre, o app para com erro e **nunca** gera uma chave nova, porque isso tornaria os dados irrecuperáveis.
   - Estado atual: Linux (libsecret) e Windows (DPAPI) implementados; o macOS (Keychain) ainda falha com erro explícito.
2. **Colunas sensíveis (`*_enc`) com AES-256-GCM** na aplicação, com uma segunda chave, também no cofre do sistema. Cada valor guarda nonce e tag de autenticação; um valor adulterado falha na leitura.
   - Formato do valor: `0x01` (versão) | nonce de 12 bytes | texto cifrado | tag de 16 bytes.
   - Dados associados (AAD): `"{tabela}.{coluna}#{id}"`. Um valor copiado para outra linha ou coluna também falha na leitura.
3. **Mascaramento:** colunas `*_masked` e as views `v_*_masked` mostram só parte do dado (ex.: `**.***.***/0001-**`) para quem não tem `sensitive.read`.
4. **Senhas** guardadas apenas como hash Argon2id, no formato PHC `$argon2id$v=19$m=65536,t=3,p=4$<salt>$<hash>` (64 MiB, 3 iterações, paralelismo 4, conforme a RFC 9106; salt de 16 bytes e hash de 32 bytes).
   - A verificação usa os parâmetros gravados na própria string, compara em tempo constante e recusa parâmetros fora de limites seguros (proteção contra uma linha adulterada que force um custo absurdo).
5. **Backups** feitos pela API de backup do SQLite a partir do banco aberto com SQLCipher, então continuam criptografados.

### 12.4 Proteção em trânsito e minimização

- Chamadas aos provedores de LLM e de embeddings só por HTTPS (TLS 1.2 ou superior).
- O contexto enviado ao LLM é montado **somente** com dados das views `v_ai_*`, trechos do RAG e memórias filtradas. Dados sensíveis nunca saem da máquina.
- Logs técnicos passam por um filtro que remove padrões de CPF, CNPJ, e-mail, telefone e chaves de API.

### 12.5 Controle de acesso

- **Autenticação local** com usuário e senha (Argon2id), bloqueio temporário após tentativas falhas e sessão que expira por inatividade:
  - **Bloqueio:** 5 falhas seguidas bloqueiam o usuário por 15 minutos (`users.failed_attempts`, `users.locked_until`). Um login correto zera o contador.
  - **Mensagem única:** a resposta é sempre "Usuário ou senha inválidos", exista o usuário ou não. Usuário inexistente também passa por uma verificação Argon2 falsa, para que o tempo de resposta não revele quem existe.
  - **Contas que não fazem login:** usuários inativos e a conta de serviço da IA (`svc.ia`).
  - **Sessão:** expira após 15 minutos sem interação de teclado ou mouse e volta à tela de login.
  - **Auditoria:** login, falha, bloqueio, saída e expiração geram eventos em `audit_log`.
- **Papéis e permissões** em `role_permissions`, com efeito `ALLOW` ou `DENY`. A regra é *deny-overrides*: um `DENY` sempre vence. A view `v_effective_permissions` calcula o resultado.
- **Escopo por linha (equivalente a RLS):** toda consulta da aplicação recebe o `warehouse_id` do usuário como parâmetro; um usuário nunca lê nem altera dados de outro armazém.
- **A IA age em nome de alguém:** o direito efetivo da IA é a **interseção** entre o papel `agente_ia` e o papel de quem fez o pedido. Ela nunca tem mais permissão do que a pessoa.
- **Segregação de funções:** quem pede uma operação crítica não pode aprová-la, e a IA nunca aprova.
- A matriz completa de papéis, com os pontos de `DENY`, está em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md).

### 12.6 Conexões ao banco

O SQLite não tem usuários de banco, então o isolamento é feito por conexões com poderes diferentes:

| Conexão | Usada por | Modo | Restrições |
|---------|-----------|------|------------|
| Leitura da IA | Ferramentas de leitura do Planner | `Mode=ReadOnly` | *Authorizer callback* (`sqlite3_set_authorizer`): só lê tabelas através das views `v_ai_*`, nega colunas sensíveis e qualquer escrita, DDL, `ATTACH` ou `PRAGMA` |
| Executor | Motor determinístico | Leitura e escrita | Só executa operações do catálogo da DSL, com consultas parametrizadas, dentro de transação |
| Aplicação | Telas e policy engine | Leitura e escrita | Todas as consultas passam pelo policy engine (permissão + escopo por armazém) |

O comportamento de referência do *authorizer* está implementado e testado em `db/tests/test_db.py` e foi portado para C# em `Core/Data`. A versão C# também nega a função `load_extension`.

Regras comuns às três conexões:
- São abertas sem pool. A chave do SQLCipher é aplicada logo após a abertura e validada com uma leitura de `sqlite_master` antes de qualquer uso.
- As conexões de escrita ligam `foreign_keys`.
- A da IA instala o *authorizer* depois da chave. Como ele nega `BEGIN`, a conexão da IA nunca abre transação.

No policy engine (`Core/Policy`):
- Consultas por armazém precisam conter o parâmetro `$warehouse_id`, que o próprio engine preenche com o armazém da sessão. Uma consulta sem esse parâmetro é recusada antes de chegar ao banco.
- Consultas de catálogo sem armazém usam uma chamada separada e explícita.
- Toda permissão negada lança exceção e gera um evento `denied` na auditoria.

### 12.7 Integridade e auditoria

- **Triggers de banco** (`db/security.sql`) bloqueiam com `RAISE(ABORT, 'DENY: ...')`:
  - UPDATE e DELETE em `audit_log` e `stock_movements`;
  - alteração de planos finalizados e exclusão de qualquer plano;
  - concessão de permissões proibidas ao papel da IA e remoção dos seus `DENY`;
  - aprovação de plano pela IA ou pelo próprio solicitante.
- **Auditoria encadeada:** cada linha de `audit_log` guarda o hash SHA-256 da linha anterior. Um job verifica a cadeia ao iniciar o app e periodicamente; qualquer quebra gera alerta ao administrador.
  - **Regra v1 do hash:** `hash` = SHA-256 em hexadecimal minúsculo dos bytes UTF-8 de
    `"nexus-audit-v1\n" + prev_hash + "\n" + F(id) F(occurred_at) F(actor_type) F(actor_id) F(on_behalf_of) F(action) F(target) F(outcome) F(details_json)`,
    em que `F(nulo) = "-;"` e `F(v) = <tamanho de v em bytes UTF-8> + ":" + v + ";"` (inteiros em decimal).
  - A primeira linha tem `prev_hash = "GENESIS"`, e cada linha seguinte guarda o `hash` da anterior (ordem por `id`).
  - O escritor insere com `BEGIN IMMEDIATE`, com `id` e `occurred_at` (`AAAA-MM-DDTHH:MM:SSZ`, UTC) explícitos, para que o hash seja calculado sobre os mesmos valores gravados.
  - Há implementações de referência iguais em Python (`db/tests/test_db.py`) e C# (`Core/Audit`); as linhas do seed já têm os hashes reais.
  - O alerta de quebra aparece no app para quem tem `audit.read`, indicando a primeira linha inválida.
- **O que é auditado:** login, cada chamada de ferramenta da IA (permitida ou negada), cada plano (submetido, rejeitado, aprovado, executado), mudanças de permissão e leituras de dados sensíveis.

### 12.8 Defesa contra prompt injection

Camadas, da mais externa para a mais interna:

1. **Conteúdo é dado, não instrução:** descrições de produtos, documentos do RAG, memórias e resultados de ferramentas entram no prompt dentro de blocos delimitados e marcados como dados de referência. O prompt de sistema diz explicitamente que instruções dentro desses blocos devem ser ignoradas e relatadas.
2. **Ferramentas limitadas pela skill:** a IA só vê as ferramentas da skill ativa.
3. **Sem SQL e sem escrita direta:** a única forma de alterar dados é `submit_plan`, com operações de um catálogo fechado.
4. **Validação determinística:** schema, regras de negócio e permissões (interseção IA ∩ usuário) são checados pelo código.
5. **Aprovação humana** para todo plano de risco crítico, com o diff simulado em linguagem de negócio.
6. **Banco:** mesmo que tudo acima falhe, a conexão da IA é somente leitura e os triggers bloqueiam as escritas proibidas.

### 12.9 Defesa contra SQL injection

- Toda consulta usa **parâmetros** (`$param` / `?`); concatenação de texto em SQL é proibida e verificada em revisão de código.
- Identificadores dinâmicos (ordenação, filtros) vêm de listas fixas no código, nunca do usuário ou do LLM.
- Argumentos de ferramentas são validados por JSON Schema (tipos, tamanhos, padrões como `^[A-Z]{3}-\d{3}$` para SKU) antes de chegar ao banco.
- A conexão da IA nega statements de escrita mesmo que um texto malicioso chegue a ela.

### 12.10 Segredos

- Chaves de API, chave do SQLCipher e chave de colunas ficam no cofre do sistema operacional.
- Nenhum segredo vai para o repositório: `.gitignore` bloqueia `.env*`, `*.key`, `*.pem` e arquivos `*.db`.
- Os dados do repositório são todos fictícios.

### 12.11 Testes de segurança

- `db/tests/test_db.py`: imutabilidade, escalonamento de privilégio, interseção IA ∩ usuário, *authorizer* da IA, SQL injection e otimização.
- Suíte de ataques de prompt injection e de SQL injection descrita em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), com métricas.
