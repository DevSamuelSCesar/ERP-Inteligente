# UML

> Diagramas UML do projeto em Mermaid. Os nomes de classes estão em inglês, como o código C# (ver [`STACK.md`](STACK.md)). Base: [`ARQUITETURA.md`](ARQUITETURA.md).

## 1. Casos de uso

O Mermaid não tem diagrama de casos de uso; ele é representado com atores à esquerda e casos de uso em formato de cápsula. `«include»` marca casos sempre executados juntos.

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

## 2. Classes do domínio (armazém)

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

## 3. Classes da camada de IA

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

## 4. Classes do motor de planos e segurança

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

## 5. Componentes e implantação

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
