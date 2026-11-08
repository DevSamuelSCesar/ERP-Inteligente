# Interface (UI)

> Organização, disposição e visibilidade dos elementos da interface do **Nexus**. Arquitetura em [`ARQUITETURA.md`](ARQUITETURA.md), stack em [`STACK.md`](STACK.md), papéis e permissões em [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md). O que **não** fazer (padrões web, SaaS e de LLM) está em [`NEGATIVAS_WEB.md`](NEGATIVAS_WEB.md).

**Nexus** é o nome do ERP e também o nome do chat, que é a tela onde o operador interage com o sistema.

## 1. Referências visuais

As imagens em [`UI/`](UI/) servem de **referência de construção**: mostram estrutura e disposição. Não servem de referência de cor e não devem ser copiadas.

| Arquivo | Tela | O que define |
|---------|------|--------------|
| [`UI/erp_ui.jpeg`](UI/erp_ui.jpeg) | Janela genérica | Coluna de menus à esquerda; título e conteúdo no restante da janela |
| [`UI/erp_ui_chat.jpeg`](UI/erp_ui_chat.jpeg) | Nexus (chat) | Título "Nexus", transcrição, campo de entrada e botão Enviar no rodapé |
| [`UI/erp_ui_chat_plan.jpeg`](UI/erp_ui_chat_plan.jpeg) | Nexus com plano | Quadro do plano dentro da transcrição, com "Aceitar?" e os botões ✓ e ✗ |
| [`UI/totvs-backoffice-linha-protheus-financeiroo.png.webp`](UI/totvs-backoffice-linha-protheus-financeiroo.png.webp) | Telas de consulta | Só a estrutura: título, abas, faixa de totais, gráfico ou tabela abaixo (os cards arredondados dela **não** são seguidos) |

---

## 2. Layout da janela

A janela tem duas regiões fixas:

```
┌──────────────┬───────────────────────────────────────────────┐
│ [≡] Nexus    │ Título da tela                                │
│ [▦] Estoque  │                                               │
│ [⌖] Endereços│                                               │
│ [▤] Pedidos  │                                               │
│ [✓] Planos   │              Container de conteúdo            │
│ [↻] Receitas │                                               │
│ [⎙] Auditoria│                                               │
│              │                                               │
│ ──────────── │                                               │
│ [☺] Usuário  │                                               │
│ [⚙] Config.  │                                               │
│ [⏻] Sair     │                                               │
└──────────────┴───────────────────────────────────────────────┘
 coluna de menus         todo o restante da janela
```

### 2.1 Coluna de menus (esquerda)

- Tem largura fixa e ocupa toda a altura da janela.
- É uma lista vertical de botões de navegação. **Cada botão tem um ícone à esquerda e o texto à direita.**
- O botão da tela atual fica destacado.
- A ordem é fixa:
  1. **Nexus**, primeiro e selecionado ao abrir o app.
  2. As telas de consulta: Estoque, Endereços, Pedidos.
  3. Planos (histórico e aprovações pendentes), Receitas, Auditoria.
  4. No rodapé, separados: o usuário logado, as configurações e o botão de sair.
- Planos mostra um contador de aprovações pendentes quando o usuário pode aprovar.
- A coluna não muda ao navegar. Só o container de conteúdo troca.

### 2.2 Container de conteúdo

- Ocupa **toda a janela após a coluna de menus**.
- Mostra o **título da tela** no topo e, abaixo dele, o conteúdo específico da tela.
- Cada tela é independente. Trocar de tela não perde o estado do Nexus: uma conversa em andamento continua lá ao voltar.

---

## 3. Arquitetura da UI (Avalonia, MVVM)

| Elemento | Responsabilidade |
|----------|------------------|
| `MainWindow` + `ShellViewModel` | Monta a coluna de menus e o container; guarda a lista de itens de menu e a página atual (`CurrentPage`) |
| `NavigationItem` | Registro de um item de menu: ícone, rótulo, tipo do ViewModel da página e a permissão exigida |
| Container | `ContentControl` ligado a `CurrentPage`; o `ViewLocator` resolve a View a partir do ViewModel |
| Página | Um par View + ViewModel por tela (`NexusViewModel`, `StockViewModel`...), com o título da tela como propriedade |
| Serviços | Os ViewModels não acessam o banco nem o LLM diretamente: usam serviços da aplicação, que passam pelo policy engine |

- A lista de itens de menu é filtrada pela permissão do usuário ao montar o shell (seção 8).
- O ViewModel do Nexus é criado uma vez por sessão, para manter a conversa ao navegar.
- Ícones são próprios, em `PathIcon`/`StreamGeometry` (seção 9.6).

---

## 4. Nexus (chat)

O Nexus é onde o operador interage com o sistema: pede em linguagem natural, acompanha o plano, aprova quando for o caso e vê o resultado.

```
┌──────────────┬───────────────────────────────────────────────┐
│  menus       │                     Nexus                     │
│              │───────────────────────────────────────────────│
│              │ 14:02  op.joao  Guarde as 40 caixas do        │
│              │                 ABC-123 que estão na doca     │
│              │ 14:02  Nexus    [quadro do plano]             │
│              │                 [resumo visual]               │
│              │ ⟳ Validando plano                             │
│              │───────────────────────────────────────────────│
│              │ [ Pedido                         ] [ Enviar ] │
└──────────────┴───────────────────────────────────────────────┘
```

- **Título** "Nexus" no topo do container.
- **Transcrição** no meio, em toda a largura, com rolagem e entradas em ordem cronológica. Cada entrada tem a hora e o autor (usuário ou Nexus) à esquerda e o conteúdo à direita. Não há balões, avatares nem alinhamento à direita para o usuário.
- As respostas do Nexus são **curtas e em texto simples**. O que é estruturado vira controle nativo: quadro do plano (seção 5) e resumo visual (seção 6). Markdown nunca é renderizado.
- A resposta aparece **completa** quando pronta, sem streaming caractere a caractere. Enquanto isso, a **linha de estado** da tarefa (abaixo da última entrada) mostra o que está acontecendo.
- **Entrada** fixa no rodapé: campo de texto em toda a largura e o botão primário **Enviar**. Enter envia e Shift+Enter quebra a linha. O campo fica desabilitado enquanto uma proposta aguarda a decisão do próprio usuário.
- Durante uma tarefa, a linha de estado tem o botão **Cancelar** (Esc), que interrompe a chamada ao provedor; nada foi gravado nesse ponto.
- O texto da transcrição pode ser selecionado e copiado (`SelectableTextBlock`).

### 4.1 Estados de uma tarefa

Cada pedido gera uma tarefa que aparece na conversa com o seu estado atual. Os estados seguem os status de plano de [`ARQUITETURA.md`](ARQUITETURA.md) (seções 4 e 11):

| Estado na UI | Status do plano | O que aparece |
|--------------|-----------------|---------------|
| Consultando | — | Linha de estado com ⟳ e a consulta em andamento ("Consultando saldo do ABC-123") |
| Planejando / validando | `validating` | Linha de estado com ⟳; correções do Validator não aparecem ao operador, a menos que o limite de tentativas seja atingido |
| Proposta | — (nada gravado) | Quadro da proposta com o efeito simulado e **Confirmar / Descartar** para quem pediu (seção 5.1) |
| Aguardando aprovação | `pending_approval` | Quadro do plano com o prompt de aprovação (seção 5) |
| Executado | `executed` | Resumo visual (seção 6) |
| Rejeitado | `rejected` | Quadro do plano marcado como rejeitado, com o motivo |
| Falhou | `failed` | Mensagem de erro em linguagem de negócio; nada foi gravado |
| Escalado | — | Aviso de que a IA não conseguiu montar um plano válido e a tarefa precisa do operador |

---

## 5. Quadro do plano e aprovação

Depois que a IA planeja e o Simulator calcula o efeito, o Nexus mostra o **quadro do plano** na transcrição: um painel em `Surface`, de cantos retos, com régua de 1 px e sem sombra.

```
┌───────────────────────────────────────────────────────┐
│ Plano 812 · armazenagem                    ⚠ crítico  │
├───────────────────────────────────────────────────────┤
│ Qtd   SKU      De        Para                         │
│  20   ABC-123  DOCA-01   A-03-02                      │
│  20   ABC-123  DOCA-01   A-03-04                      │
├───────────────────────────────────────────────────────┤
│ Aceitar?  [✓ Aceitar · Alt+A]  [✗ Rejeitar · Alt+R]   │
└───────────────────────────────────────────────────────┘
```

- O plano aparece **em linguagem de negócio**, montado a partir do diff simulado, em linhas tabulares (Barlow Semi Condensed com `tnum`). JSON e nomes de operações não aparecem; o número do plano aparece só como referência para a auditoria.
- O cabeçalho mostra a skill e o nível de risco do plano, que é o maior risco entre os passos.
- A aprovação segue [`ARQUITETURA.md`](ARQUITETURA.md) (seções 4 e 12.5):
  - **Plano reversível:** o quadro aparece como prévia, sem prompt, e o plano é executado em seguida.
  - **Plano crítico:** o quadro mostra **"Aceitar?"** com os botões ✓ (aceitar) e ✗ (rejeitar). **A execução só acontece depois do aceite.**
- **Segregação de funções:** os botões só ficam ativos para quem tem `plan.approve` **e não é o solicitante**. Para o solicitante, o quadro mostra "aguardando supervisor" no lugar dos botões. A IA nunca aprova.
- O supervisor recebe o plano pendente na tela Planos e abre o mesmo quadro para decidir.
- **Rejeitar** abre um diálogo nativo que pede um motivo curto, que vira memória (seção 8 de [`ARQUITETURA.md`](ARQUITETURA.md)).
- Os botões funcionam pelo teclado: Alt+A aceita e Alt+R rejeita, com foco visível.
- Depois da decisão, o quadro fica só para leitura e mostra quem decidiu e quando.
- **Cores** (seção 9):
  - **✓ Aceitar:** botão preenchido em `StatusSuccess`.
  - **✗ Rejeitar:** botão neutro com contorno, nunca vermelho, porque rejeitar é uma decisão legítima e não um erro.
  - **Plano crítico:** régua superior de 2 px e o texto "⚠ crítico" em `StatusWarning` (âmbar), porque risco não é erro.

### 5.1 Proposta do Nexus (confirmação de quem pediu)

O plano montado pela IA chega primeiro como **proposta**: o motor já o validou e simulou, mas nada foi gravado.

- O quadro é o mesmo da seção 5, com o título "Proposta do Nexus · skill", as linhas do plano e o **efeito simulado** (os mesmos visuais do resumo da seção 6).
- O rodapé pergunta "Confirmar?" com **✓ Confirmar** (Alt+C, `StatusSuccess`) e **Descartar** (Alt+D, contorno neutro, nunca vermelho).
- **Confirmar** envia o plano ao motor com origem `ai` (direitos IA ∩ usuário). Um plano reversível executa e aparece o quadro do plano com o resumo. Um plano crítico vai para a aprovação de um supervisor (seção 5).
- **Descartar** encerra a proposta só para leitura, com "Descartado. Nada foi gravado."
- Configurar o provedor é feito em Configurações, seção "Provedor de LLM": só o administrador digita a chave (campo mascarado, nunca reexibida) e escolhe o modelo; os demais veem só se há chave.

---

## 6. Resumo visual pós-execução

Depois da execução, o Nexus mostra um **resumo visual do que foi operado**. O resumo é uma visualização do sistema, não um texto. Ele é montado a partir do diff do Simulator e do resultado do Executor, ou seja, dos dados gravados, nunca do texto do LLM.

Elementos:

- **Faixa de totais** no topo: uma linha de pares rótulo/valor (quantidade movimentada, endereços afetados, SKUs afetados, risco), com algarismos tabulares.
- **Visualização por operação**, conforme a tabela abaixo.
- Um **link para o plano** na tela Planos/Auditoria.

| Operação | Visualização |
|----------|--------------|
| `receive_goods` | Doca com os itens recebidos (SKU, lote, quantidade) |
| `move_stock` | Fluxo de/para entre endereços (DOCA-01 → A-03-02) com a quantidade em cada seta |
| `reserve_stock` | Barra de saldo do SKU dividida em livre e reservado, antes e depois |
| `pick_items` | Lista de endereços separados com a quantidade retirada de cada um |
| `ship_order` | Pedido expedido com os itens e a baixa de saldo |
| `adjust_stock` | Barras de saldo antes/depois por endereço, com o motivo do ajuste |
| `create_location` / `create_product` | Ficha do cadastro criado (campos em pares rótulo/valor) |

Quando o plano tem vários passos, o resumo agrupa por SKU e endereço em vez de repetir uma visualização por passo.

As visualizações usam só a paleta de dados (`Data1`–`Data5`, seção 9.4). "Antes" é sempre `Data5` (cinza) e "depois" é `Data1`. Saídas e baixas de estoque **não** ficam vermelhas: saída não é um problema.

---

## 7. Telas de consulta

Estoque, Produtos, Endereços, Pedidos, Planos, Receitas e Auditoria seguem a mesma estrutura dentro do container (referência TOTVS, só estrutura):

```
Título da tela
[ aba 1 ][ aba 2 ][ aba 3 ]                 [ filtro ▾ ]
──────────────────────────────────────────────────────────
Saldo total  12.480   Endereços  312   Lotes a vencer  14
──────────────────────────────────────────────────────────
SKU      Descrição          Endereço   Lote     Qtd
ABC-123  Caixa padrão       A-03-02    L2410    20
...      (DataGrid)
```

- **Título**, depois **abas ou filtros**, depois a **faixa de totais** e, por fim, a **tabela (`DataGrid`) ou o gráfico** principal, em toda a largura.
- **Produtos** lista o catálogo inteiro, inclusive produtos recém-cadastrados ainda sem saldo, com o saldo no armazém do usuário. O custo unitário (dado sensível) nunca aparece nessa tela.
- As telas de consulta são **somente leitura**. Qualquer alteração de dados passa pelo Nexus (plano) ou por uma receita disparada pela UI, que gera um plano validado da mesma forma (seção 7 de [`ARQUITETURA.md`](ARQUITETURA.md)).
- As tabelas são `DataGrid` virtualizadas, com cabeçalho fixo, ordenação, colunas redimensionáveis, navegação por setas e linhas de ~28 px. Não há paginação de site nem "carregar mais". Os filtros vêm de listas fixas (seção 12.9 de [`ARQUITETURA.md`](ARQUITETURA.md)).

---

## 8. Visibilidade

O que cada usuário vê depende das permissões efetivas (`v_effective_permissions`) e do armazém dele.

| Elemento | Regra |
|----------|-------|
| Itens da coluna de menus | Só aparecem os itens cuja permissão o usuário tem (ex.: Auditoria exige `audit.read`; Receitas exige `recipe.manage` para editar) |
| Botões ✓ / ✗ do plano | Ativos só com `plan.approve` e quando o usuário não é o solicitante |
| Contador de pendências em Planos | Só para quem pode aprovar |
| Dados sensíveis (CPF, CNPJ completo, contato, custo unitário) | Mascarados para quem não tem `sensitive.read` |
| Dados de outro armazém | Nunca aparecem: toda consulta usa o `warehouse_id` do usuário |
| Configurações de provedores de LLM e de usuários | Só para quem tem `user.manage` / `role.manage` |

A UI **esconde** o que o usuário não pode usar, mas isso é só conveniência. A regra de verdade continua no policy engine e no banco (defesa em profundidade, seção 12.1 de [`ARQUITETURA.md`](ARQUITETURA.md)).

---

## 9. Identidade visual

Estética premium e imponente: **preto-grafite** como base e **vermelho carmim** como cor da marca. O desafio é que o vermelho também é a cor universal de erro. Por isso, cada cor tem **um papel fixo**, e o vermelho da marca nunca é usado para comunicar estado.

### 9.1 Princípios

1. **Hierarquia 60-30-10.**
   - Cerca de 60% é fundo grafite.
   - Cerca de 30% são superfícies cinza (coluna de menus, quadros, tabelas).
   - No máximo 10% é vermelho da marca.

   O vermelho é escasso de propósito: é o que dá o ar premium e faz ele chamar atenção para a ação principal.
2. **Uma cor, um papel.** A marca identifica e conduz a ação; as cores semânticas comunicam estado; a paleta de dados representa dados. Os três grupos nunca se misturam.
3. **Nunca só a cor.** Todo estado tem **ícone + texto** além da cor (✓ executado, ⚠ crítico, ⊗ falhou, ⟳ em andamento). A tela continua compreensível para daltônicos e em monitores ruins.
4. **Contraste antes de estética.** Todo par de cores usado passa no WCAG 2.2 (seção 9.5).
5. **A hierarquia de informação vem de tamanho, peso e posição, não de cor.** A cor destaca exceções, não estrutura a tela.

### 9.2 Tokens de cor (tema escuro)

As Views usam **só os nomes dos tokens**, nunca valores hexadecimais.

| Token | Hex | Uso |
|-------|-----|-----|
| `Background` | `#0E0F11` | Fundo do container de conteúdo |
| `SurfaceSidebar` | `#15171A` | Coluna de menus |
| `Surface` | `#1C1F23` | Quadro do plano, faixa de totais, tabelas, campo de entrada |
| `SurfaceRaised` | `#262A2F` | Hover, linhas selecionadas, menus suspensos |
| `Border` | `#353A40` | Réguas de 1 px entre regiões e linhas de tabela (nunca a única pista de um controle) |
| `TextPrimary` | `#F2F3F5` | Títulos, valores, texto principal |
| `TextSecondary` | `#A9AFB7` | Rótulos, legendas, metadados |
| `TextDisabled` | `#80868E` | Controles desabilitados |
| `BrandPrimary` | `#C8102E` | Preenchimento do botão primário, logotipo, barra do item ativo no menu |
| `BrandAccent` | `#E01E3C` | Hover do botão primário, anel de foco |
| `OnBrand` | `#FFFFFF` | Texto e ícones sobre `BrandPrimary` / `BrandAccent` |
| `StatusSuccess` | `#3DD68C` | Executado, botão Aceitar |
| `StatusWarning` | `#F5A524` | Risco crítico, aguardando aprovação, atenção |
| `StatusError` | `#FF6B5E` | Falhou, erro de validação, conexão perdida |
| `StatusInfo` | `#5B9BF0` | Em andamento, consultando, informação |
| `OnStatus` | `#0E0F11` | Texto e ícones sobre um preenchimento semântico |

Mensagens de estado são **linhas**: o ícone e a primeira palavra ("Falhou", "Crítico") ficam na cor semântica, e o restante do texto fica em `TextPrimary`. Não há fundo tingido, barra lateral colorida nem caixa (ver [`NEGATIVAS_WEB.md`](NEGATIVAS_WEB.md), seção 3).

### 9.3 Vermelho da marca × vermelho de erro

A separação é feita em três camadas ao mesmo tempo:

| Camada | Marca (`BrandPrimary`) | Erro (`StatusError`) |
|--------|------------------------|----------------------|
| **Lugar** | Só no chrome: coluna de menus, logotipo, botão primário, foco | Só dentro do conteúdo, em alertas e no estado "falhou" |
| **Forma** | Preenchimento sólido com texto branco, ou barra fina | Linha de mensagem com ícone ⊗ e texto explicando o erro |
| **Tom** | Carmim profundo, frio | Coral, quente e claro |

Regras:

- O vermelho da marca **nunca** aparece em status, números, gráficos, valores negativos ou saídas de estoque.
- **Risco crítico é âmbar, não vermelho.** Crítico é "exige atenção e aprovação", não "deu errado".
- **Rejeitar não é vermelho.** É uma decisão válida e usa o botão neutro com contorno.
- **Ações destrutivas** (ex.: excluir rascunho) usam botão neutro com ícone e um diálogo de confirmação, não um botão vermelho.
- Na mesma área, só pode haver **um** botão primário em vermelho, a ação principal da tela.

### 9.4 Paleta de dados

Gráficos e o resumo visual (seção 6) usam uma paleta categórica **sem vermelho e sem as cores semânticas**. Assim, uma barra nunca parece um alerta.

| Token | Hex | Uso |
|-------|-----|-----|
| `Data1` | `#8DA4C4` | Série principal, valor "depois" |
| `Data2` | `#2BB3A3` | Segunda série (ex.: entradas) |
| `Data3` | `#A58BF0` | Terceira série (ex.: saídas) |
| `Data4` | `#C9A66B` | Quarta série |
| `Data5` | `#8A9099` | Valor "antes", referência, "outros" |

Com mais de cinco séries, agrupe o excedente em "outros" (`Data5`). Rótulos de valores e eixos usam `TextSecondary`.

### 9.5 Contraste (WCAG 2.2)

Mínimos: **4,5:1** para texto (meta **7:1** no texto principal) e **3:1** para ícones, controles, foco e marcas de gráfico. As razões foram calculadas com a fórmula de luminância relativa do WCAG.

| Par (frente sobre fundo) | Razão | Mínimo |
|--------------------------|-------|--------|
| `TextPrimary` sobre `Background` / `Surface` / `SurfaceRaised` | 17,27 / 14,90 / 13,00 | 7 |
| `TextSecondary` sobre `Background` / `Surface` / `SurfaceRaised` | 8,68 / 7,48 / 6,53 | 4,5 |
| `TextDisabled` sobre `Surface` | 4,50 | 3 (isento; mantido legível) |
| `OnBrand` sobre `BrandPrimary` / `BrandAccent` | 5,88 / 4,75 | 4,5 |
| `BrandPrimary` sobre `SurfaceSidebar` (barra do item ativo) | 3,05 | 3 |
| `BrandAccent` sobre `Surface` / `SurfaceRaised` (anel de foco) | 3,48 / 3,04 | 3 |
| `StatusSuccess` / `StatusWarning` / `StatusError` / `StatusInfo` sobre `Surface` | 8,82 / 8,10 / 5,92 / 5,82 | 4,5 |
| `OnStatus` sobre `StatusSuccess` / `StatusWarning` | 10,22 / 9,40 | 4,5 |
| `Data1`–`Data5` sobre `Surface` | 6,49 / 6,36 / 5,95 / 7,21 / 5,14 | 3 |

`BrandPrimary` sobre `Surface` dá só 2,81:1, por isso o **anel de foco usa `BrandAccent`**, nunca `BrandPrimary`. O vermelho da marca também nunca é usado como cor de texto.

### 9.6 Tipografia, ícones e forma

- **Fonte:** família **Barlow** (SIL OFL), embutida no app como `AvaloniaResource`. Confirmado nos arquivos da fonte: tem algarismos tabulares (`tnum`) e todos os acentos do português. Motivo e lista de fontes proibidas em [`NEGATIVAS_WEB.md`](NEGATIVAS_WEB.md), seção 7.
- **Escala:**

  | Uso | Fonte, tamanho e peso |
  |-----|-----------------------|
  | Título da tela | Barlow Condensed, 26 px, SemiBold |
  | Título de quadro | Barlow Condensed, 17 px, SemiBold |
  | Texto e transcrição | Barlow, 14 px, Regular |
  | Rótulos | Barlow, 12 px, Medium, em `TextSecondary` |
  | Tabelas e quadro do plano | Barlow Semi Condensed, 13 px, Regular, `tnum` |
  | Valores da faixa de totais | Barlow Semi Condensed, 18 px, SemiBold, `tnum` |

- **Ícones:** desenho próprio em `PathIcon`/`StreamGeometry`, numa grade de 16 px com traço de 1,5 px e terminações retas, em `TextSecondary`. No item ativo do menu, o ícone fica em `TextPrimary`. Nada de Lucide, Heroicons, Material ou emojis.
- **Forma:**
  - Cantos de **2 px** em botões e campos e de **0 px** em painéis, quadros e tabelas.
  - Espaçamento em grade de 4/8 px, densidade alta (linhas de tabela de ~28 px).
  - Sem sombras, sem gradientes e sem blur; a profundidade vem da sequência `Background` → `Surface` → `SurfaceRaised` e de réguas de 1 px.
- **Movimento:** nenhuma transição nem animação de entrada/saída. A única animação é o indicador de progresso (⟳) da linha de estado.

### 9.7 O que nunca fazer

- Usar `BrandPrimary` para erro, alerta, número negativo ou série de gráfico.
- Comunicar um estado só pela cor, sem ícone e texto.
- Colocar dois botões primários vermelhos na mesma área.
- Usar vermelho no botão Rejeitar ou no selo de risco crítico.
- Escrever valores hexadecimais em Views; só tokens.
- Criar cores fora desta tabela sem atualizar esta seção e recalcular o contraste.
- Qualquer padrão listado em [`NEGATIVAS_WEB.md`](NEGATIVAS_WEB.md); toda tela nova passa pela checklist da seção 9 desse documento.

### 9.8 Implementação (Avalonia)

- Os tokens ficam num `ResourceDictionary` (`Styles/Tokens.axaml`) dentro de `ThemeDictionaries`, na chave `Dark`, como `Color` + `SolidColorBrush` (ex.: `BrandPrimaryBrush`). O app força `RequestedThemeVariant="Dark"`.
- As Views referenciam os pincéis por `{DynamicResource ...}`. Isso permite adicionar depois um tema claro (útil sob luz forte de armazém) só com uma nova chave `Light`, sem tocar nas telas.
- A base é o `FluentTheme` do Avalonia, com os recursos dele sobrescritos: `ControlCornerRadius` = 2 e `OverlayCornerRadius` = 0. As transições dos estilos de controle são removidas (`Transitions` vazias).
- O `Button` tem `ControlTheme` próprio (`Styles/ButtonTheme.axaml`), que substitui o do Fluent: o tema do Fluent pinta uma camada branca translúcida sobre o conteúdo nos estados desabilitado, hover e pressionado, e essa camada não pode ser sobrescrita por estilos. O botão primário desabilitado fica sem preenchimento, com régua `Border` e texto em `TextDisabled`; só fica vermelho quando a ação está disponível.
- Estilos de controle (botão primário, botão neutro, linha de mensagem, quadro, faixa de totais) ficam em classes reutilizáveis (`Classes="primary"`, `Classes="message error"`...). Uma tela não define cor própria.
- As fontes Barlow ficam em `Assets/Fonts/` como `AvaloniaResource`. `Program.cs` define a Barlow como fonte padrão (`FontManagerOptions`), o que inclui popups e tooltips. Os recursos `TextFont`, `TitleFont`, `TabularFont` e `TabularStrongFont` ficam em `Styles/Tokens.axaml`. Os cortes condensados são endereçados pelo arquivo (`.../BarlowCondensed-SemiBold.ttf#Barlow Condensed SemiBold`), porque a busca pela pasta casa "Barlow SemiBold" no lugar deles.
- A Barlow não tem os glifos ✓ ⚠ ⊗ →: estados e setas são sempre ícones de `Styles/Icons.axaml`, nunca caracteres Unicode.
