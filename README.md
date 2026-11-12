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
            CHK["knowledge/*.md<br/>chunking por seção ##"]
            EMB["Embeddings opcionais<br/>OpenAI 1.536 d · local 384 d"]
            HYB["Busca híbrida<br/>BM25 (FTS5) + cosseno (sqlite-vec) · RRF"]
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
    PL -- "propose_plan" --> VAL
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
        +CompleteAsync(LlmRequest request) Task~LlmResponse~
    }
    class DeepSeekProvider {
        -string Endpoint
        +TestConnectionAsync() Task
    }
    class LlmSettings {
        +bool IsConfigured
        +string Model
        +SetKey(string key)
        +ClearKey()
    }
    ILlmProvider <|.. DeepSeekProvider : feature 3
    DeepSeekProvider --> LlmSettings : chave no cofre do SO

    class Planner {
        +RunAsync(UserSession user, PlannerTurn[] history, string request) Task~PlannerResult~
        +propose_plan()
        +offer_options()
    }
    class PlannerResult {
        <<abstract>>
    }
    class PlannerAnswer
    class PlannerProposals {
        +ProposalItem[] Items
        +string? Text
        +PlannerOption[] Options
    }
    class PlannerEscalated
    class PlannerFailed
    PlannerResult <|-- PlannerAnswer
    PlannerResult <|-- PlannerProposals
    PlannerResult <|-- PlannerEscalated
    PlannerResult <|-- PlannerFailed
    class PlannerTools {
        +search_products()
        +get_product()
        +get_stock()
        +search_locations()
        +search_zones()
        +search_lots()
        +search_orders()
        +search_partners()
    }
    class AiReadConnection {
        +Authorize(action, table, column, view) AuthorizerResult
    }
    class PlanEngine {
        +Preview(user, planJson, plannedBy) EnginePreview
    }

    class RecipeMatcher {
        +MatchAsync(UserRequest request) Task~Plan?~
    }
    class SkillCatalog {
        +Index(rights) SkillIndexEntry[]
        +Load(name, rights) SkillContent
    }
    class KnowledgeRetriever {
        +Search(query, limit, minScore) KnowledgeHit[]
    }
    class MemoryRetriever {
        +Search(user, query, options) MemoryHit[]
    }
    class MemoryStore {
        +Remember(user, kind, scope, scopeRef, content, origin) MemoryWriteResult
        +MarkUsed(user, ids)
    }

    Planner --> ILlmProvider
    Planner --> PlannerTools : leitura
    Planner --> PlanEngine : propose_plan (só simula)
    Planner ..> PlannerResult
    PlannerTools --> AiReadConnection
    Planner --> SkillCatalog : índice e load_skill
    Planner --> KnowledgeRetriever : contexto e search_knowledge
    Planner --> MemoryRetriever : contexto e search_memories
    Planner --> MemoryStore : remember
    RecipeMatcher ..> ILlmProvider : feature 7
```

### Classes do motor de planos e segurança

```mermaid
classDiagram
    class PlanDocument {
        +string DslVersion
        +string Intent
        +string? Skill
        +PlanStepDocument[] Steps
        +ToJson() string
    }
    class PlanStepDocument {
        +int Index
        +string Op
        +JsonObject Args
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
        +PlanDocument Template
        +JsonSchema Parameters
        +RecipeStatus Status
    }
    class DslParser {
        +Parse(string json) DslParseResult
    }
    class OperationCatalog {
        +OperationDefinition[] V1
        +TryGet(string op) OperationDefinition
    }
    class OperationDefinition {
        +string Name
        +RiskLevel Risk
        +string Permission
        +JsonSchema ArgsSchema
    }
    class IOperationHandler {
        <<interface>>
        +string Op
        +Validate(EngineContext ctx, PlanStepDocument step, JsonObject args) PlanError[]
        +Apply(EngineContext ctx, PlanStepDocument step, JsonObject args) string
    }
    class ReceiveGoodsHandler
    class MoveStockHandler
    class AdjustStockHandler
    class CreateProductHandler
    class CreateLocationHandler
    IOperationHandler <|.. ReceiveGoodsHandler
    IOperationHandler <|.. MoveStockHandler
    IOperationHandler <|.. AdjustStockHandler
    IOperationHandler <|.. CreateProductHandler
    IOperationHandler <|.. CreateLocationHandler

    class PlanRunner {
        +Run(EngineContext ctx, PlanDocument plan, isAllowed) RunResult
    }
    class PlanSimulator {
        +Simulate(UserSession requester, PlanDocument plan, isAllowed) RunResult
    }
    class PlanExecutor {
        +Execute(UserSession requester, PlanDocument plan, long planId, isAllowed, onSuccess, onFailure) RunResult
    }
    class PlanEngine {
        +Preview(UserSession requester, string json) EnginePreview
        +Submit(UserSession requester, string json, string plannedBy) EngineSubmission
        +Approve(UserSession approver, long planId)
        +Reject(UserSession approver, long planId, string reason)
    }
    class PlanDiff {
        +BalanceDelta[] Balances
        +MoveDelta[] Moves
        +CreatedRecord[] Created
        +ToSummary(RiskLevel risk) ExecutionSummary
    }
    class PolicyEngine {
        +IsAllowed(UserSession s, string permission) bool
        +Require(UserSession s, string permission)
        +EffectiveForAi(UserSession user) string[]
        +PermissionsForRole(long roleId) string[]
    }
    class UserSession {
        +long UserId
        +long RoleId
        +long WarehouseId
        +string[] Permissions
    }
    class AuditLog {
        +Append(AuditEntry entry) long
        +AppendWithin(connection, transaction, AuditEntry entry) long
    }
    class AuditVerifier {
        +Verify(connection) AuditVerification
    }
    class ColumnCipher {
        +Encrypt(string plain, string table, string column, long id) byte[]
        +Decrypt(byte[] value, string table, string column, long id) string
    }

    PlanDocument *-- PlanStepDocument
    Recipe --> PlanDocument
    DslParser --> OperationCatalog
    OperationCatalog *-- OperationDefinition
    OperationDefinition --> RiskLevel
    PlanRunner --> IOperationHandler
    PlanRunner --> PlanDiff
    PlanSimulator --> PlanRunner
    PlanExecutor --> PlanRunner
    PlanEngine --> DslParser
    PlanEngine --> PlanSimulator
    PlanEngine --> PlanExecutor
    PlanEngine --> PolicyEngine
    PlanEngine --> AuditLog
    CreateProductHandler --> ColumnCipher
    PolicyEngine --> UserSession
    AuditVerifier --> AuditLog
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
- **RAG:** chunks por seção `##` dos 22 procedimentos, indexados na abertura, com busca FTS5/BM25 sem acento; memórias e trechos relevantes entram no pedido como dados delimitados. Com embeddings ligados em Configurações (OpenAI `text-embedding-3-small` ou um modelo local em `127.0.0.1`), a busca é híbrida: top-20 do BM25 + top-20 por cosseno (distância pelo `sqlite-vec`), combinados por *Reciprocal Rank Fusion*; a pergunta vai mascarada e, sem embeddings, a busca continua só BM25.
- **Memórias:** por armazém, gravadas pela IA (`remember`) e pelo sistema (rejeições) através de um filtro que recusa dados pessoais e texto com cara de instrução; só o supervisor corrige e arquiva, na tela Memórias.
- **Fine-tuning:** um modelo aberto pequeno com LoRA transforma pedidos em `{intent, recipe, params}`. Métricas: *exact match*, F1 por campo e % de JSON válido, comparados com o Haiku em *few-shot*.
- **Dados:** 22 documentos **fictícios** em [`knowledge/`](knowledge/README.md) (procedimentos e políticas de um centro de distribuição inventado).
- **Ferramentas da IA:** oito de leitura (`search_products`, `get_product`, `get_stock`, `search_locations`, `search_zones`, `search_lots`, `search_orders`, `search_partners`), `load_skill` (corpo de uma skill sob demanda), `propose_plan` (até 3 alternativas, só simula), `propose_skill` (rascunho de skill, só para o supervisor), `search_knowledge` e `search_memories` (BM25), `remember` (memória pelo filtro de proteção) e `offer_options` (botões). A IA só vê o que o direito efetivo permite.
- **Skills (feature 4):** procedimentos no formato `SKILL.md` dos agentes de IA (`nome`, `descricao`, `operacoes` e corpo em Markdown), em português. O prompt leva só o índice e a IA carrega o corpo quando precisa. As 6 iniciais vêm embutidas; as criadas ficam no banco cifrado, com versões e auditoria. Só o supervisor (`skill.manage`) cria e edita, na tela Skills ou confirmando uma skill redigida pela IA; os demais papéis só leem.
- **Modo proativo:** assume padrões razoáveis e lista as suposições. Se a zona, o endereço ou o produto não existe, propõe o plano que o cria antes da movimentação.
- **Pontos difíceis:** alucinação, pedidos fora do escopo e prompt injection, com métricas e conjuntos de avaliação em [`references/IA.md`](references/IA.md), seção 5. Os conjuntos (50 pedidos com gabarito, 40 perguntas sobre os procedimentos, 30 fora do escopo e 10 proativos) ficam em `tools/eval` e rodam com `tools/Nexus.Eval`. Medido sem LLM: o RAG acha o documento certo entre os 4 primeiros em 100% das perguntas; em 1º lugar, 80% só com BM25 e 87,5% na busca híbrida com o modelo local.

## Parte de Segurança

- **Modelo de dados** com cada coluna classificada como pública, interna ou sensível e a proteção dos sensíveis (AES-GCM, mascaramento, SQLCipher): [`references/SEGURANCA_DADOS.md`](references/SEGURANCA_DADOS.md), seção 2.
- **Matriz de papéis:** operador, supervisor, administrador, auditor e agente IA, com os pontos de `DENY` (seção 3).
- **Catálogo de operações:** 20 operações da DSL v1, cada uma com permissão e risco. Quatro são críticas e exigem outro supervisor:

| Risco | Operações |
|-------|-----------|
| Crítico | `adjust_stock`, `cancel_inbound_order`, `ship_order`, `cancel_outbound_order` |
| Reversível | `receive_goods`, `move_stock`, `create_product`, `update_product`, `create_supplier`, `create_customer`, `create_zone`, `update_zone`, `create_location`, `create_location_range`, `update_location`, `create_inbound_order`, `create_outbound_order`, `reserve_stock`, `release_reservation`, `pick_items` |

Permissão e regras de cada uma em [`references/ARQUITETURA.md`](references/ARQUITETURA.md), seção 5.
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
- **Implementado no app (feature 2 do roadmap): motor determinístico de planos.**
  - **DSL e validação:** DSL JSON v1 validada por JSON Schema, com limites contra entrada maliciosa, e por regras de negócio (saldo disponível, capacidade, lote e validade, escopo do armazém).
  - **Simulação:** roda o mesmo código da execução, dentro de uma transação desfeita, então a pré-visualização mostra exatamente o que vai acontecer.
  - **Execução:** transacional, tudo ou nada, com a auditoria gravada na mesma transação.
  - **Aprovação:**
    - planos críticos (`adjust_stock`, `cancel_inbound_order`, `ship_order`, `cancel_outbound_order`) esperam um supervisor diferente de quem pediu;
    - a IA nunca aprova;
    - o que pode rodar é decidido pelos direitos de quem pediu, não de quem aprova.
  - **Tela "Novo plano":** formulários gerados do catálogo, com listas de escolha e o próximo código livre. Monta o plano sem LLM, mostra a pré-visualização e envia.
  - **Atualização de bancos antigos:** um banco existente é atualizado na abertura por migrações versionadas (`db/migrations/`), até a versão 8 (consumo de tokens).

## Parte de integração

- **Diagrama único** juntando banco, LLM e RAG, com permissões, proteção de dados e auditoria marcadas: seção [Diagrama da arquitetura](#diagrama-da-arquitetura) acima, incluindo os ataques.
- **Matriz ampliada com a IA:** a IA lê só as views `v_ai_*` por uma conexão somente leitura e age com a **interseção** dos seus direitos com os de quem pediu. Ela nunca aprova nada, não vê dados sensíveis e não grava skills (as próprias instruções). Desde 2026-11-08 ela tem os direitos operacionais do usuário (ajuste, expedição, cadastros e pedidos), com confirmação de quem pediu e aprovação de outro supervisor para críticos.
- **Ataques testados:** prompt injection (5 vetores) e SQL injection + escalonamento de privilégio, automatizados em `db/tests/test_db.py` (115 testes) e nos testes do app em `tests/Nexus.Tests` (1425 testes, incluindo o *authorizer* da IA, o bloqueio de login, o isolamento entre armazéns, a detecção de adulteração da auditoria, planos maliciosos e a segregação de funções na aprovação): [`references/SEGURANCA_DADOS.md`](references/SEGURANCA_DADOS.md), seção 6.
- **Suíte de 25 ataques com métricas (feature 6):** o modelo é simulado obedecendo à injeção, e as defesas fora dele seguram tudo. Resultado: **0% de sucesso dos ataques e 100% de detecção**. Textos suspeitos nos dados são marcados, auditados como `ai.injection` e avisados ao operador. A auditoria mascara CPF, CNPJ, e-mail, telefone e chaves antes do hash.
- **Operação (feature 9):**
  - **Backup:** backup criptografado diário pela API do SQLite (mesma chave do SQLCipher, 7 cópias). O administrador também faz e restaura backups, e a restauração é verificada antes.
  - **Memórias:** um job diário arquiva memórias repetidas, parecidas ou sem uso.
  - **Auditoria:** painel com filtros e verificação da cadeia.
  - **Custos:** painel com tokens e custo estimado por papel e modelo, a partir de `llm_usage`.
  - **Instaladores:** Windows (zip e Inno Setup) e Linux (tar.gz com `install.sh`), *self-contained*.
- **Multi-provedor e fine-tuning (feature 8):** DeepSeek, Claude (pelo SDK oficial da Anthropic), OpenAI e um modelo local ajustado. O administrador escolhe, em Configurações, o provedor e o modelo do planejamento e do reconhecimento de receitas. As chaves ficam no cofre do sistema, uma por provedor. O experimento de fine-tuning do Matcher está em `tools/finetune`. O Qwen2.5-1.5B com LoRA foi treinado numa GTX 1660 (3 h) e teve 100% no teste gerado. Nos 40 pedidos escritos à mão, teve 80% de *exact match*, porque inventava o valor que faltava. O app passou a recusar todo valor que não está no pedido (`RecipeGrounding`, para qualquer provedor), e com isso o resultado subiu para 92,5%. A comparação com o Haiku depende da chave da Anthropic ([`IA.md`](references/IA.md), seção 4).
- **Receitas (feature 7):** 3 planos iguais executados viram uma receita candidata, que só o supervisor promove. Uma receita ativa é usada pela tela Receitas sem LLM, ou reconhecida pelo Recipe Matcher (uma chamada curta ao modelo rápido, sem ferramentas) antes do planejamento. O plano gerado passa pelo motor como qualquer outro. A receita é suspensa quando a DSL ou a skill muda, e despromovida após 3 falhas seguidas.

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

No primeiro uso o app cria o banco criptografado a partir dos scripts de `db/` e guarda as chaves no cofre do sistema. Para entrar, use um dos usuários fictícios (`op.joao`, `sup.maria`, `adm.carlos`, `aud.lucia`) com a **senha de demonstração `demo`**. Essa senha existe só para os dados fictícios do repositório e nunca deve ser usada com dados reais.

Para o **Nexus** conversar, o administrador (`adm.carlos`) cola a chave de API da DeepSeek em **Configurações → Provedor de LLM**. A chave vai para o cofre do sistema, nunca para o banco nem para o repositório. Depois disso, peça em linguagem natural. Exemplos:
- "guarde as 40 caixas do ABC-123 da doca em A-02-01": o Nexus consulta o estoque pelas views `v_ai_*` e devolve uma **proposta** com o efeito simulado;
- "guarde 10 cx do ABC-123 em Q-01-01", com a zona Q ainda inexistente: o Nexus propõe, num só plano, a zona Q, os endereços e a movimentação, e lista as suposições (capacidade, por exemplo);
- "quais endereços da zona A têm espaço para 40 caixas?": o Nexus consulta e responde, e pode oferecer os próximos passos como botões.

Nada é gravado até quem pediu clicar em **Confirmar**. Quando há mais de uma forma de fazer, o Nexus mostra até três propostas para escolher. Cada proposta traz o porquê e as suposições assumidas; SKU, endereço, zona e pedido aparecem destacados na resposta.

Ajustes, expedições e cancelamentos também podem ser pedidos, desde que quem pede tenha o direito. Planos críticos ficam em **Planos** esperando a aprovação de outro supervisor. A tela **Novo plano** continua disponível para montar qualquer plano sem IA. Arquivos `.db` são ignorados pelo git.

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
