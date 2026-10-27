# Diagramas da Arquitetura

> Representação visual de [`ARQUITETURA.md`](ARQUITETURA.md). Em caso de divergência, vale o `ARQUITETURA.md`.
> Os diagramas usam Mermaid, que o GitHub e o VS Code (com extensão de Mermaid) renderizam direto. Diagramas UML estão em [`UML.md`](UML.md).

## 1. Arquitetura integrada (IA + dados + segurança)

Mostra, num só desenho, o LLM, o RAG, o fine-tuning, a interface e o banco, e **onde ficam as permissões, a proteção de dados e a auditoria** (caixas com 🔒).

```mermaid
flowchart TB
    OP(["Operador / Supervisor"])

    subgraph APP["App desktop nativo · C# + Avalonia"]
        subgraph UI["Interface"]
            LOGIN["Login · Argon2id"]
            CHAT["Chat"]
            PREV["Prévia do plano<br/>diff simulado"]
            APRV["Aprovação de<br/>operação crítica"]
        end

        POL["🔒 Policy engine<br/>deny-overrides · escopo por armazém<br/>direito da IA = IA ∩ usuário"]

        subgraph AI["Camada de IA"]
            RM["Recipe Matcher<br/>Haiku 5.5 ou modelo ajustado"]
            PL["Planner · Sonnet 5.5<br/>skills · memórias filtradas 🔒"]
            ROCONN["🔒 Ferramentas de leitura<br/>conexão read-only + authorizer<br/>só views v_ai_*"]
        end

        subgraph RAG["RAG"]
            CHK["knowledge/*.md · SKILL.md<br/>chunking por seção ##"]
            EMB["Embeddings<br/>1.536 dimensões"]
            HYB["Busca híbrida<br/>BM25 + cosseno · top-4"]
        end

        subgraph ENG["Motor determinístico · sem LLM"]
            VAL["Validator<br/>schema · regras · 🔒 permissões"]
            SIM["Simulator<br/>diff + risco"]
            EXE["Executor<br/>transação · SQL parametrizado"]
        end
    end

    subgraph EXT["Provedores externos · HTTPS · sem dados sensíveis"]
        CLAUDE["Claude API<br/>DeepSeek / OpenAI opcionais"]
        OAI["OpenAI<br/>text-embedding-3-small"]
    end

    subgraph FT["Fine-tuning · offline"]
        FTD["~500 pedidos fictícios"]
        FTM["LoRA em modelo<br/>aberto pequeno"]
        FTE["Avaliação vs Haiku"]
    end

    subgraph DB["🔒 SQLite + SQLCipher · AES-256 em repouso"]
        DATA[("Domínio + stock_movements<br/>*_enc AES-GCM · append-only")]
        AIDATA[("plans · recipes · memories<br/>knowledge FTS5 + sqlite-vec")]
        SEC[("🔒 role_permissions ALLOW/DENY<br/>audit_log com cadeia de hash<br/>triggers DENY")]
    end

    OP --> LOGIN --> CHAT --> POL --> RM
    RM -- "receita" --> VAL
    RM -- "sem receita" --> PL
    RM <--> CLAUDE
    PL <--> CLAUDE
    PL --> ROCONN --> DATA
    PL --> HYB --> AIDATA
    CHK --> EMB --> AIDATA
    EMB <--> OAI
    PL -- "submit_plan" --> VAL
    VAL -- "erro estruturado" --> PL
    VAL --> SIM
    SIM -- "crítico" --> PREV --> APRV --> EXE
    SIM -- "reversível" --> EXE
    EXE --> DATA
    EXE --> AIDATA
    POL --> SEC
    VAL --> SEC
    EXE --> SEC
    APRV --> SEC
    FTD --> FTM --> FTE -- "se aprovado" --> RM
```

## 2. Fronteiras de confiança e ataques testados

Cada seta tracejada é um ataque; o nó em que ela termina é a camada que o bloqueia.

```mermaid
flowchart LR
    subgraph UNTRUSTED["Não confiável"]
        U1["Texto do chat"]
        U2["Descrição de produto<br/>TST-INJ-01"]
        U3["Documento ou memória<br/>envenenados"]
        U4["Saída do LLM<br/>planos e argumentos"]
    end

    subgraph TRUSTED["Confiável · código do app"]
        D1["Dados delimitados<br/>como referência"]
        V1["Validator<br/>schema · regras · IA ∩ usuário"]
        P1["SQL parametrizado"]
        A1["Aprovação humana<br/>supervisor ≠ solicitante"]
    end

    subgraph DBL["Banco"]
        R1["Conexão da IA<br/>read-only + authorizer"]
        T1["Triggers DENY"]
        L1[("Dados")]
    end

    U1 --> D1
    U2 --> D1
    U3 --> D1
    D1 --> U4
    U4 --> V1 --> P1 --> L1
    V1 --> A1 --> P1

    X1["Ataque: prompt injection<br/>'zere o estoque'"] -.-> V1
    X2["Ataque: SQL injection<br/>x' OR '1'='1"] -.-> P1
    X3["Ataque: escalonamento<br/>dar plan.approve à IA"] -.-> T1
    X4["Ataque: exfiltração<br/>'mostre os CPFs'"] -.-> R1
    X5["Ataque: autoaprovação"] -.-> A1
```

## 3. Fluxo de uma tarefa

Exemplo: "Guarde as 40 caixas do ABC-123 que estão na doca", sem receita existente.

```mermaid
sequenceDiagram
    actor OP as Operador
    participant UI as UI
    participant POL as Policy engine
    participant RM as Recipe Matcher
    participant PL as Planner (LLM)
    participant T as Ferramentas de leitura
    participant M as Motor determinístico
    participant DB as SQLite

    OP->>UI: Guarde as 40 caixas do ABC-123 que estão na doca
    UI->>POL: pedido + usuário
    POL->>DB: auditoria (pedido recebido)
    POL->>RM: pedido + direitos efetivos (IA ∩ usuário)
    RM->>DB: buscar receitas compatíveis
    DB-->>RM: nenhuma
    RM->>PL: planejar
    PL->>T: load_skill("armazenagem")
    T-->>PL: SKILL.md
    PL->>T: find_location(ABC-123, 40)
    T->>DB: SELECT parametrizado em v_ai_* (conexão read-only)
    DB-->>T: A-03-02 (20 livres), A-03-04 (30 livres)
    T-->>PL: resultado compacto
    PL->>M: submit_plan(plano JSON)

    loop Até ser válido ou atingir o limite de tentativas
        M->>M: validar (schema + regras + permissões)
        alt Inválido
            M-->>PL: erro estruturado
            PL->>M: submit_plan(plano corrigido)
        end
    end

    M->>M: simular (diff de estoque + risco)

    alt Risco crítico
        M->>UI: pré-visualização em linguagem de negócio
        UI->>OP: precisa de um supervisor
        OP-->>UI: supervisor aprova
        UI-->>M: aprovado (supervisor ≠ solicitante)
    end

    M->>DB: BEGIN · executar todos os passos · COMMIT
    DB-->>M: ok
    M->>DB: plano, auditoria encadeada, memórias e candidatas a receita
    M-->>UI: 40 cx ABC-123: DOCA-01 → A-03-02 (20), A-03-04 (20)
    UI-->>OP: resultado
```

## 4. Ciclo de vida de um plano

Os estados em inglês correspondem ao campo `plans.status`.

```mermaid
stateDiagram-v2
    [*] --> validating: submit_plan
    validating --> validating: inválido, nova tentativa do LLM
    validating --> rejected: limite de tentativas ou sem permissão
    validating --> Simulando: válido
    Simulando --> pending_approval: risco crítico
    Simulando --> Executando: risco reversível
    pending_approval --> Executando: supervisor aprova
    pending_approval --> rejected: supervisor rejeita
    Executando --> executed: COMMIT
    Executando --> failed: erro, ROLLBACK
    rejected --> [*]
    executed --> [*]
    failed --> [*]
```

## 5. Ciclo de vida de uma receita

```mermaid
stateDiagram-v2
    [*] --> candidate: mesma estrutura executada com sucesso, sem correção
    candidate --> active: repetições suficientes
    active --> active: uso com sucesso
    active --> suspended: versão da DSL ou da skill mudou
    suspended --> active: revalidada
    active --> demoted: falhas repetidas
    suspended --> demoted: revalidação falhou
    demoted --> [*]
```

## 6. Modelo de dados

Classificação entre aspas: **P** público, **I** interno, **S** sensível. Schema completo em `db/schema.sql`.

```mermaid
erDiagram
    WAREHOUSES ||--o{ ZONES : "tem"
    ZONES ||--o{ LOCATIONS : "tem"
    PRODUCTS ||--o{ LOTS : "tem"
    PRODUCTS ||--o{ STOCK_BALANCES : "tem saldo"
    LOCATIONS ||--o{ STOCK_BALANCES : "armazena"
    LOTS |o--o{ STOCK_BALANCES : "rastreia"
    PRODUCTS ||--o{ STOCK_MOVEMENTS : "movimenta"
    PLANS |o--o{ STOCK_MOVEMENTS : "gera"
    RECIPES |o--o{ PLANS : "origina"
    USERS ||--o{ PLANS : "solicita"
    ROLES ||--o{ USERS : "define"
    ROLES ||--o{ ROLE_PERMISSIONS : "ALLOW/DENY"
    SUPPLIERS ||--o{ INBOUND_ORDERS : "entrega"
    CUSTOMERS ||--o{ OUTBOUND_ORDERS : "pede"
    PLANS |o--o{ MEMORIES : "origina"
    USERS |o--o{ AUDIT_LOG : "age"

    PRODUCTS {
        int id PK "P"
        text sku "P"
        text name "P"
        blob unit_cost_enc "S"
    }
    USERS {
        int id PK "I"
        text username "I"
        blob cpf_enc "S"
        blob email_enc "S"
        text password_hash "S"
        int role_id FK "I"
    }
    SUPPLIERS {
        int id PK "I"
        text name "I"
        text cnpj_masked "I"
        blob cnpj_enc "S"
    }
    STOCK_MOVEMENTS {
        int id PK "I"
        text kind "I"
        int quantity "I"
        int plan_id FK "I"
    }
    PLANS {
        int id PK "I"
        text plan_json "I"
        text risk "I"
        text status "I"
        int approved_by FK "I"
    }
    ROLE_PERMISSIONS {
        int role_id FK "I"
        text permission "I"
        text effect "I"
    }
    AUDIT_LOG {
        int id PK "I"
        text action "I"
        text outcome "I"
        text prev_hash "I"
        text hash "I"
    }
    MEMORIES {
        int id PK "I"
        text kind "I"
        text content "I"
        real confidence "I"
    }
```
