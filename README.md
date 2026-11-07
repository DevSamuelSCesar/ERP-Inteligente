# Nexus: armazém gerenciado por IA

**Grupo:** ERP INTELIGENTE · **Repositório:** https://github.com/NatanCorreiaS/erp-inteligente

| Componente | Matrícula |
|------------|-----------|
| Natan Correia da Silva | 231071692 |
| Clara Gabryellen Paixão Aderno | 231070720 |
| Samuel Santos César | 232070317 |
| Rayan Santos Silva | 231070220 |

Entrega para as duas disciplinas: **Projeto de Inteligência Artificial** e **Segurança e Otimização de BD** (parte comum, parte de IA, parte de Segurança e parte de integração).

## Problema

Armazéns pequenos e médios controlam estoque com planilhas ou sistemas cheios de telas, que exigem treinamento e geram erros de digitação, estoque divergente e produtos vencidos. O **Nexus** é um app desktop em que o operador pede em português ("guarde as 40 caixas do ABC-123 que estão na doca") e uma IA consulta os dados, monta um plano, mostra o efeito e o executa **com segurança**. O supervisor só decide o que é crítico.

**Ideia central:** o LLM **descreve** as alterações em um plano JSON; um motor de código **valida, simula e executa** o plano numa transação. A IA nunca escreve no banco diretamente.

## Canvas

| Item | Resumo |
|------|--------|
| Usuário | Operadores e supervisores de armazém, sem equipe de TI |
| Entrada → saída | Pedido em linguagem natural → plano validado, estoque atualizado, resposta simples, auditoria |
| Arquitetura | App Avalonia → camada de IA (Planner, skills, RAG, memórias) → motor determinístico → SQLite + SQLCipher |
| Maior risco | A IA alterar o estoque de forma errada ou não autorizada (alucinação ou prompt injection) |
| MVP em 3 semanas | Recebimento e armazenagem via chat, motor de planos, RAG sobre os procedimentos, aprovação de críticos e auditoria |

Canvas completo, com a arquitetura em caixas: [`references/CANVAS.md`](references/CANVAS.md).

## Diagrama da arquitetura

Arquitetura integrada: LLM, RAG, fine-tuning, interface e banco, com permissões, proteção de dados e auditoria marcadas com 🔒. Outros diagramas (fluxo de uma tarefa, ciclos de vida, dados) em [`references/DIAGRAMAS.md`](references/DIAGRAMAS.md).

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

### Fronteiras de confiança e ataques testados

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

## UML

### Casos de uso

```mermaid
flowchart LR
    OPE["👤 Operador"]
    SUP["👤 Supervisor"]
    ADM["👤 Administrador"]
    AUD["👤 Auditor"]
    IA["🤖 Agente IA"]

    subgraph SYS["Nexus · módulo de armazém"]
        UC1(["Fazer pedido em linguagem natural"])
        UC2(["Consultar estoque, lotes e pedidos"])
        UC3(["Receber mercadoria"])
        UC4(["Armazenar / transferir"])
        UC5(["Reservar e separar pedido"])
        UC6(["Expedir pedido"])
        UC7(["Ajustar estoque"])
        UC8(["Aprovar operação crítica"])
        UC9(["Planejar e enviar plano"])
        UC10(["Consultar procedimentos (RAG)"])
        UC11(["Gravar memória"])
        UC12(["Gerenciar usuários e papéis"])
        UC13(["Gerenciar cadastros"])
        UC14(["Consultar auditoria"])
        UC15(["Autenticar"])
    end

    OPE --> UC1
    OPE --> UC2
    OPE --> UC3
    OPE --> UC4
    OPE --> UC5
    SUP --> UC1
    SUP --> UC6
    SUP --> UC7
    SUP --> UC8
    SUP --> UC14
    ADM --> UC12
    ADM --> UC13
    ADM --> UC14
    AUD --> UC2
    AUD --> UC14

    IA --> UC9
    IA --> UC2
    IA --> UC10
    IA --> UC11

    UC1 -. "«include»" .-> UC15
    UC1 -. "«include»" .-> UC9
    UC6 -. "«include»" .-> UC8
    UC7 -. "«include»" .-> UC8
```

### Classes do domínio (armazém)

```mermaid
classDiagram
    class Warehouse {
        +int Id
        +string Code
        +string Name
    }
    class Zone {
        +int Id
        +string Code
        +string Name
    }
    class Location {
        +int Id
        +string Code
        +int Capacity
        +bool IsActive
        +FreeCapacity(StockBalance[] balances) int
    }
    class Product {
        +int Id
        +string Sku
        +string Name
        +string Unit
        +bool TracksExpiry
        -EncryptedValue UnitCost
    }
    class Lot {
        +int Id
        +string LotCode
        +DateOnly? ExpiresOn
        +IsExpired(DateOnly today) bool
    }
    class StockBalance {
        +int Quantity
        +int ReservedQty
        +int AvailableQty
    }
    class StockMovement {
        +long Id
        +MovementKind Kind
        +int Quantity
        +string? Reason
        +DateTime CreatedAt
    }
    class MovementKind {
        <<enumeration>>
        Receive
        Move
        Pick
        Ship
        Adjust
    }
    class InboundOrder {
        +string DocumentNumber
        +InboundStatus Status
    }
    class OutboundOrder {
        +string OrderNumber
        +OutboundStatus Status
    }
    class Supplier {
        +string Name
        +string CnpjMasked
        -EncryptedValue Cnpj
    }
    class Customer {
        +string Name
        +string DocumentMasked
        -EncryptedValue Document
    }

    Warehouse "1" *-- "*" Zone
    Zone "1" *-- "*" Location
    Product "1" *-- "*" Lot
    StockBalance "*" --> "1" Product
    StockBalance "*" --> "1" Location
    StockBalance "*" --> "0..1" Lot
    StockMovement "*" --> "1" Product
    StockMovement --> MovementKind
    InboundOrder "*" --> "1" Supplier
    OutboundOrder "*" --> "1" Customer
```

### Classes da camada de IA

```mermaid
classDiagram
    class ILlmProvider {
        <<interface>>
        +ChatAsync(request) Task~LlmResponse~
        +CountTokensAsync(request) Task~int~
    }
    class AnthropicProvider
    class OpenAiCompatibleProvider {
        +Uri BaseUrl
    }
    ILlmProvider <|.. AnthropicProvider
    ILlmProvider <|.. OpenAiCompatibleProvider : OpenAI e DeepSeek

    class Planner {
        +PlanAsync(UserRequest request) Task~PlanResult~
    }
    class RecipeMatcher {
        +MatchAsync(UserRequest request) Task~Plan?~
    }
    class SkillRegistry {
        +Index() SkillSummary[]
        +Load(string name) Skill
    }
    class Skill {
        +string Name
        +string Version
        +string Instructions
        +string[] AllowedTools
        +string[] AllowedOperations
    }
    class IReadTool {
        <<interface>>
        +string Name
        +JsonSchema InputSchema
        +ExecuteAsync(JsonElement args) Task~JsonElement~
    }
    class AiReadConnection {
        +Authorize(action, table, column, view) AuthorizerResult
    }
    class KnowledgeRetriever {
        +SearchAsync(string query, int k) Task~Chunk[]~
    }
    class MemoryStore {
        +SearchAsync(string query, Scope scope) Task~Memory[]~
        +RememberAsync(Memory candidate) Task
    }
    class MemoryFilter {
        +IsAcceptable(Memory candidate) bool
    }

    Planner --> ILlmProvider
    Planner --> SkillRegistry
    Planner --> IReadTool
    Planner --> KnowledgeRetriever
    Planner --> MemoryStore
    RecipeMatcher --> ILlmProvider
    SkillRegistry --> Skill
    IReadTool --> AiReadConnection
    MemoryStore --> MemoryFilter
```

### Classes do motor de planos e segurança

```mermaid
classDiagram
    class Plan {
        +string Intent
        +PlanStep[] Steps
        +string DslVersion
        +RiskLevel Risk
        +PlanStatus Status
    }
    class PlanStep {
        +string Op
        +JsonElement Args
        +string? As
    }
    class RiskLevel {
        <<enumeration>>
        Read
        Reversible
        Critical
    }
    class Recipe {
        +string Name
        +Plan Template
        +JsonSchema Parameters
        +RecipeStatus Status
    }
    class IOperation {
        <<interface>>
        +string Name
        +RiskLevel Risk
        +string RequiredPermission
        +Validate(PlanStep step, StockSnapshot state) ValidationResult
        +Apply(PlanStep step, DbTransaction tx)
    }
    class MoveStockOperation
    class ReceiveGoodsOperation
    class AdjustStockOperation
    IOperation <|.. MoveStockOperation
    IOperation <|.. ReceiveGoodsOperation
    IOperation <|.. AdjustStockOperation

    class PlanValidator {
        +Validate(Plan plan, Principal principal) ValidationResult
    }
    class PlanSimulator {
        +Simulate(Plan plan) StockDiff
    }
    class ApprovalService {
        +RequestAsync(Plan plan, StockDiff diff) Task~Approval~
    }
    class PlanExecutor {
        +ExecuteAsync(Plan plan) Task~ExecutionResult~
    }
    class PolicyEngine {
        +IsAllowed(Principal p, string permission) bool
        +ForAi(User requester) Principal
        +WarehouseScope(Principal p) int
    }
    class Principal {
        +User User
        +bool IsAi
        +string[] EffectivePermissions
    }
    class AuditLog {
        +AppendAsync(AuditEntry entry) Task
        +VerifyChainAsync() Task~bool~
    }
    class ColumnCipher {
        +Encrypt(string plain) EncryptedValue
        +Decrypt(EncryptedValue value) string
    }

    Plan *-- PlanStep
    Plan --> RiskLevel
    Recipe --> Plan
    PlanValidator --> IOperation
    PlanValidator --> PolicyEngine
    PlanSimulator --> Plan
    PlanExecutor --> IOperation
    PlanExecutor --> AuditLog
    PlanExecutor --> ColumnCipher
    ApprovalService --> PolicyEngine
    ApprovalService --> AuditLog
    PolicyEngine --> Principal
```

### Componentes e implantação

```mermaid
flowchart TB
    subgraph POSTO["Posto do armazém · Windows, Linux ou macOS"]
        subgraph PROC["Processo do app · .NET + Avalonia"]
            C_UI["UI (MVVM)"]
            C_AI["Camada de IA"]
            C_ENG["Motor de planos"]
            C_SEC["Policy engine · ColumnCipher · AuditLog"]
        end
        F_DB[("erp.db<br/>SQLite + SQLCipher")]
        F_SK["skills/ e knowledge/<br/>Markdown + JSON"]
        KS["Cofre do sistema<br/>DPAPI · Keychain · libsecret"]
    end

    subgraph NET["Internet · HTTPS"]
        API1["api.anthropic.com"]
        API2["api.openai.com"]
        API3["api.deepseek.com (opcional)"]
    end

    subgraph TRAIN["Máquina de treino · offline"]
        FT["Fine-tuning LoRA<br/>gera modelo local para receitas"]
    end

    C_UI --> C_AI --> C_ENG --> F_DB
    C_UI --> C_SEC
    C_ENG --> C_SEC --> F_DB
    C_AI --> F_SK
    C_SEC --> KS
    C_AI --> API1
    C_AI --> API2
    C_AI -.-> API3
    FT -. "modelo exportado" .-> C_AI
```

## Tecnologia

App **desktop nativo** em **C# + Avalonia** (não é web nem SaaS). Banco **SQLite** criptografado com **SQLCipher**, com FTS5 e `sqlite-vec`. LLMs: **Claude Sonnet 5.5** (planejamento) e **Claude Haiku 5.5** (receitas), com DeepSeek e OpenAI configuráveis. Embeddings: `text-embedding-3-small`. SQL Server, MongoDB e n8n **não** são usados; a seção de Segurança mostra como cada mecanismo pedido foi implementado no SQLite. Detalhes em [`references/STACK.md`](references/STACK.md).

## Parte de IA

- **Diagrama da arquitetura** (LLM, RAG, fine-tuning e interface): seção [Diagrama da arquitetura](#diagrama-da-arquitetura) acima.
- **LLM e custo:** Sonnet 5.5 + Haiku 5.5 custam **≈ US$ 30,90 por 1.000 usos**, caindo para ≈ US$ 17,75 quando 60% dos pedidos viram receitas. Comparativo com Opus 5.5, Haiku puro e DeepSeek em [`references/IA.md`](references/IA.md).
- **RAG:** chunks por seção `##`, embeddings de 1.536 dimensões, `sqlite-vec` como banco vetorial, busca híbrida BM25 + cosseno com top-4.
- **Fine-tuning:** um modelo aberto pequeno com LoRA transforma pedidos em `{intent, recipe, params}`. Métricas: *exact match*, F1 por campo e % de JSON válido, comparados com o Haiku em *few-shot*.
- **Dados:** 22 documentos **fictícios** em [`knowledge/`](knowledge/README.md) (procedimentos e políticas de um centro de distribuição inventado).
- **Pontos difíceis:** alucinação, pedidos fora do escopo e prompt injection, com métricas e conjuntos de avaliação em [`references/IA.md`](references/IA.md), seção 5.

## Parte de Segurança

- **Modelo de dados** com cada coluna classificada como pública, interna ou sensível e a proteção dos sensíveis (AES-GCM, mascaramento, SQLCipher): [`references/SEGURANCA_DADOS.md`](references/SEGURANCA_DADOS.md), seção 2.
- **Matriz de papéis:** operador, supervisor, administrador, auditor e agente IA, com os pontos de `DENY` (seção 3).
- **Ameaças e gargalo**, e como provamos a defesa (seção 4).
- **Otimização medida** com 1 milhão de movimentos (seção 5):

| Consulta | Antes | Depois |
|----------|------:|-------:|
| Q1 kardex | 72,4 ms | 0,39 ms |
| Q2 FEFO | 33,8 ms | 9,4 ms |

- **Base inicial:** scripts em [`db/`](db/): `schema.sql`, `security.sql` (triggers e views), `seed.sql` (dados fictícios), `volume.sql`, `benchmark.sql` e `optimize.sql`.
- **Implementado no app (feature 1 do roadmap):**
  - **Banco e chaves:** SQLCipher no arquivo inteiro, com duas chaves aleatórias de 32 bytes guardadas no cofre do sistema (libsecret no Linux, DPAPI no Windows). Uma cópia do `.db` não abre ("file is not a database"), e o app nunca gera uma chave nova para um banco existente.
  - **Login e sessão:**
    - senhas em Argon2id (64 MiB, t=3, p=4);
    - bloqueio de 15 min após 5 falhas;
    - mensagem única, sem revelar se o usuário existe;
    - sessão que expira após 15 min sem uso.
  - **Três conexões:**
    - a da IA é somente leitura, com *authorizer* que só deixa ler as views `v_ai_*`;
    - a do Executor e a da Aplicação são de escrita, com `foreign_keys` ligado.
  - **Policy engine:** confere a permissão no banco a cada consulta (*deny-overrides*) e preenche o `$warehouse_id` da sessão; a IA age com a interseção dos direitos dela com os do usuário.
  - **Auditoria encadeada:** regra v1 de SHA-256, igual em Python e C#, verificada ao abrir o app e a cada login; uma cadeia quebrada aparece como alerta para quem tem `audit.read`.
  - Regras e números completos em [`references/ARQUITETURA.md`](references/ARQUITETURA.md), seção 12.

## Parte de integração

- **Diagrama único** juntando banco, LLM e RAG, com permissões, proteção de dados e auditoria marcadas: seção [Diagrama da arquitetura](#diagrama-da-arquitetura) acima, incluindo os ataques.
- **Matriz ampliada com a IA:** a IA lê só as views `v_ai_*` por uma conexão somente leitura e age com a **interseção** dos seus direitos com os de quem pediu. Ela nunca aprova nada nem vê dados sensíveis.
- **Ataques testados:** prompt injection (5 vetores, meta de 0% de sucesso) e SQL injection + escalonamento de privilégio, automatizados em `db/tests/test_db.py` (27 testes) e nos testes do app em `tests/Nexus.Tests` (267 testes, incluindo o *authorizer* da IA, o bloqueio de login, o isolamento entre armazéns e a detecção de adulteração da auditoria): [`references/SEGURANCA_DADOS.md`](references/SEGURANCA_DADOS.md), seção 6.

## Como rodar

```bash
# Criar e carregar o banco
sqlite3 erp.db < db/schema.sql
sqlite3 erp.db < db/security.sql
sqlite3 erp.db < db/seed.sql

# Volume e otimização
sqlite3 erp.db < db/volume.sql
sqlite3 erp.db < db/benchmark.sql   # antes
sqlite3 erp.db < db/optimize.sql
sqlite3 erp.db < db/benchmark.sql   # depois

# Testes de segurança e otimização
python3 -m unittest discover -s db/tests -v

# Gerar o PDF deste README, com os diagramas (precisa de Chrome/Chromium; o PDF não vai para o git)
python3 tools/readme_to_pdf.py               # gera README.pdf

# App desktop (.NET 10)
dotnet run                                   # banco em ~/.local/share/Nexus/nexus.db (Linux)
dotnet run -- --db /caminho/nexus.db         # outro arquivo
dotnet test tests/Nexus.Tests                # testes do app
```

No primeiro uso o app cria o banco criptografado a partir dos scripts de `db/` e guarda as chaves no cofre do sistema. Para entrar, use um dos usuários fictícios (`op.joao`, `sup.maria`, `adm.carlos`, `aud.lucia`) com a **senha de demonstração `nexus-demo-2026`**. Essa senha existe só para os dados fictícios do repositório e nunca deve ser usada com dados reais.

O Nexus ainda só consulta: alterar dados chega com o motor de planos (feature 2 do roadmap). Arquivos `.db` são ignorados pelo git.

## Documentação

| Documento | Conteúdo |
|-----------|----------|
| [`ARQUITETURA.md`](references/ARQUITETURA.md) | Arquitetura completa, incluindo segurança de dados (seção 12) |
| [`DIAGRAMAS.md`](references/DIAGRAMAS.md) | Arquitetura integrada, ataques, fluxo, ciclos de vida, dados |
| [`UML.md`](references/UML.md) | Casos de uso, classes, componentes e implantação |
| [`CANVAS.md`](references/CANVAS.md) | Canvas de uma página |
| [`IA.md`](references/IA.md) | LLM, custos, RAG, fine-tuning, riscos e métricas |
| [`SEGURANCA_DADOS.md`](references/SEGURANCA_DADOS.md) | Classificação, papéis, ameaças, otimização, ataques |

## Apresentação parcial (5 minutos)

1. **Problema e usuário** (1 min): canvas.
2. **Arquitetura** (2 min): diagrama integrado; o LLM planeja e o código executa.
3. **Segurança e IA** (1 min): matriz de papéis com a IA e ataques testados.
4. **Próximos passos** (1 min): MVP em 3 semanas.

## Regras atendidas

Não é web nem SaaS (app desktop nativo). Todos os dados são fictícios. Não há chaves de API nem senhas no repositório: as chaves ficam no cofre do sistema operacional.
