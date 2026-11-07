# Negativas: web, SaaS e estética padrão de LLM

> O Nexus é um **aplicativo desktop nativo em Avalonia, renderizado pelo Skia**. Não é um site, não é um SaaS e não tem nenhuma lógica web por trás. Este documento lista o que **não** fazer na interface e o que fazer no lugar. Complementa [`UI.md`](UI.md) (layout e identidade visual) e [`STACK.md`](STACK.md).

Por que este documento existe: LLMs e kits de UI tendem a reproduzir o mesmo visual de produto web, com gradiente roxo, cards arredondados, fonte Inter, toasts e balões de chat. Isso deixa o produto genérico e, num app nativo, também piora o uso: menos densidade, menos teclado e comportamento de navegador onde não há navegador. Toda tela nova passa pela checklist da seção 9.

---

## 1. Lógica web

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| `WebView`, HTML, CSS, JavaScript, Electron, Blazor Hybrid ou qualquer motor de navegador embutido | Somente controles do Avalonia desenhados pelo Skia |
| Renderizar Markdown ou respostas do LLM como HTML | Converter a resposta em controles nativos (`TextBlock`, `SelectableTextBlock`, quadros do plano, resumo visual) |
| Fontes, ícones, imagens ou scripts carregados de CDN ou da rede | Tudo embutido no assembly como `AvaloniaResource`; o app funciona offline, exceto as chamadas ao LLM |
| Pensar em DOM, `div`, flexbox, `rem`, `em`, `vw`, `%` de viewport | `Grid`, `DockPanel`, `StackPanel`, `GridSplitter`, tamanhos em unidades independentes de dispositivo e linhas/colunas `*`/`Auto` |
| Breakpoints responsivos de celular/tablet/desktop, menu hambúrguer | Janela de desktop com **tamanho mínimo** definido; o conteúdo redistribui por `Grid` e a coluna de menus nunca colapsa em hambúrguer |
| Roteamento por URL, botão "voltar", histórico do navegador, deep links | Navegação pelo shell (`CurrentPage` no `ShellViewModel`); o estado de cada tela vive no seu ViewModel |
| Links azuis sublinhados como forma principal de navegação | Botões da coluna de menus e comandos; link só para abrir um registro relacionado (ex.: o plano na auditoria), com estilo do tema |
| Classes utilitárias no estilo Tailwind, CSS-in-XAML, valores mágicos repetidos em cada View | Tokens e classes de estilo centralizadas (`UI.md`, seção 9.8) |
| Formulários com `submit` de página, recarregar a tela para atualizar | Bindings MVVM; os dados atualizam no lugar |
| `localStorage`/cookies como mentalidade de estado | Estado no ViewModel e persistência no SQLite pelo serviço da aplicação |

---

## 2. Estética padrão de LLM

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Gradiente roxo→azul ou roxo→rosa, "brilho de IA", aura, glow ao redor de botões | Cores planas da seção 9 do `UI.md`; nenhum gradiente |
| ✨, estrelas, varinha mágica ou cérebro para representar IA | A IA é o Nexus e usa o mesmo vocabulário visual do resto do sistema; nenhum ícone de "mágica" |
| Glassmorphism, `backdrop-blur`, transparência acrílica em painéis | Superfícies opacas (`Background` → `Surface` → `SurfaceRaised`) |
| Sombras suaves e grandes em tudo, elevação flutuante | Sem sombras; profundidade por plano de superfície e régua de 1 px |
| Cantos muito arredondados (8–24 px), botões em pílula, avatares redondos | Cantos de **2 px** em controles e **0 px** em painéis, quadros e tabelas |
| Paleta slate/zinc/gray do Tailwind, "dark mode" com acento roxo ou índigo | Grafite e carmim próprios do Nexus (`UI.md`, seção 9.2) |
| Fontes Inter, Geist, Poppins, Manrope, IBM Plex, Roboto, Space Grotesk, DM Sans, Satoshi; JetBrains Mono para código | Família **Barlow** embutida (seção 7) |
| Ícones Lucide, Heroicons, Feather, Material Symbols ou emojis | Conjunto próprio de ícones em `PathIcon`/`StreamGeometry`, desenhado na grade do Nexus |
| Emojis em rótulos, títulos, mensagens e estados ("🚀 Pronto!") | Texto e ícone do sistema; nenhum emoji na interface |
| Cabeçalho "hero" centralizado, título gigante com subtítulo cinza | Título da tela alinhado à esquerda, no topo do container, sem subtítulo decorativo |
| Conteúdo numa coluna estreita centralizada com margens enormes | Conteúdo em toda a largura do container; a densidade é uma qualidade num ERP |
| Espaço em branco excessivo, uma informação por linha | Grade de 4/8 px com densidade alta (linhas de tabela de ~28 px) |
| Texto cinza-claro de baixo contraste "para parecer elegante" | Contraste WCAG medido (`UI.md`, seção 9.5) |
| Ilustrações genéricas (pessoas com membros longos, blobs, 3D isométrico) | Nenhuma ilustração; estados vazios em texto (seção 3) |

---

## 3. Padrões de SaaS

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Tudo dentro de cards com borda, cantos e sombra | Painéis retos separados por régua; cards só não existem |
| Grade de "KPI cards" com número grande, seta de delta verde/vermelha e sparkline | **Faixa de totais**: uma linha horizontal de pares rótulo/valor, com algarismos tabulares e sem setas coloridas |
| Badges e pílulas coloridas em cada linha de tabela | Estado como texto + ícone na coluna de estado; cor só quando o estado exige atenção |
| Callout com barra lateral colorida e fundo tingido | Linha de mensagem com ícone e texto na cor semântica, sem fundo nem barra (`UI.md`, seção 9.2) |
| Toasts empilhados no canto da tela | O resultado aparece **onde a ação aconteceu** (no quadro do plano, na linha da tarefa); avisos persistentes vão para a linha de estado da tela |
| Modal com fundo escurecido e desfocado sobre a página | Diálogo nativo (`Window.ShowDialog`) quando uma decisão precisa bloquear; o resto acontece na própria tela |
| Menu "⋯" que esconde as ações principais | Ações visíveis na tela; o menu de contexto nativo (`ContextMenu`) só repete atalhos |
| Botão flutuante (FAB) | Ação principal fixa no lugar da tela (ex.: Enviar no rodapé do Nexus) |
| Skeleton com shimmer, spinners de página inteira | Os dados existentes continuam na tela; um indicador de progresso pequeno aparece **só** na área que está esperando |
| "Carregar mais", rolagem infinita, paginação de site | `DataGrid` virtualizada com rolagem contínua e filtros |
| Estado vazio com ilustração e botão "Comece agora!" | Uma linha de texto dizendo o que está vazio e como preencher |
| Tour de onboarding, tooltips em bolha que apontam para botões, checklist de "primeiros passos" | Rótulos claros e tooltips nativos curtos no hover/foco |
| Textos de marketing: "Bem-vindo de volta! 👋", "Ops! Algo deu errado 😅", "Turbine sua produtividade" | Linguagem operacional, direta e em português: "Plano executado", "Falha ao gravar: endereço A-03-02 bloqueado" |
| Avatar redondo com a inicial do usuário, foto de perfil | Nome e papel do usuário em texto no rodapé da coluna de menus |
| Seletor claro/escuro na interface, banners de "novidades", upgrade de plano | Um tema; nada de upsell ou comunicação de produto dentro do ERP |
| Barra de busca global no topo com ⌘K como peça central | Filtros em cada tela; atalhos de teclado documentados |
| Barra de título personalizada imitando macOS (bolinhas coloridas) | Barra de título e decorações de janela do sistema operacional |

---

## 4. Clichês de chat de IA

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Balões à esquerda e à direita, com "cauda" e avatar | **Transcrição** em largura total: cada entrada tem o rótulo do autor (operador ou Nexus) e a hora, alinhados à esquerda |
| Coluna central estreita (~768 px) no meio da tela | Toda a largura do container de conteúdo |
| Indicador "digitando…" com três pontos animados | Linha de estado da tarefa: "Consultando saldo do ABC-123", "Validando plano" |
| Texto em streaming caractere a caractere com cursor piscando | A resposta aparece completa quando pronta; o progresso é mostrado pela linha de estado |
| Fileira de botões copiar / regenerar / 👍 / 👎 sob cada resposta | Nada sob as respostas; o feedback do operador é aceitar ou rejeitar o plano (com motivo) |
| Chips de sugestões de prompt ("Experimente perguntar…") | Campo de texto vazio com um placeholder curto; receitas aparecem nas telas como comandos nativos |
| Seletor de modelo de LLM dentro do chat | O modelo é configuração do sistema (tela de Configurações, com permissão) |
| Respostas longas em Markdown com títulos, negrito e listas | Respostas curtas em texto simples; o conteúdo estruturado vira quadro do plano ou resumo visual |
| "Como uma IA, eu…", desculpas, entusiasmo artificial | Tom operacional e objetivo, definido no prompt de sistema |
| Tela inicial do chat com saudação gigante centralizada | O Nexus abre com a transcrição da sessão e o campo de entrada pronto |

---

## 5. Cores e bordas

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Cinza sobre cinza com contraste insuficiente | Pares de cor com contraste medido (`UI.md`, seção 9.5) |
| Cor como decoração (ícones coloridos no menu, cabeçalhos coloridos) | Cor só com papel: marca, estado ou dado (`UI.md`, seção 9.1) |
| Vermelho da marca usado para erro, exclusão ou valor negativo | Regras da seção 9.3 do `UI.md` |
| Verde/vermelho para entradas/saídas e altas/baixas | Paleta de dados neutra (`UI.md`, seção 9.4) |
| Borda de 1 px **e** sombra **e** fundo diferente no mesmo elemento | Um recurso de separação por vez: plano de superfície **ou** régua de 1 px (`Border`) |
| Bordas em todos os lados de cada controle, criando "caixas dentro de caixas" | Réguas só onde separam regiões (coluna de menus × conteúdo, cabeçalho × corpo, linhas de tabela) |
| Contorno de foco removido ou trocado por glow | Anel de foco de 2 px em `BrandAccent`, sempre visível ao navegar por teclado |
| Gradientes, texturas, ruído, padrões de fundo | Superfícies planas |

---

## 6. Comportamento e interação

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Ações que só aparecem no hover | Ações visíveis; o hover só realça |
| Interface pensada só para mouse | **Teclado primeiro:** ordem de Tab coerente, aceleradores com Alt nos menus, atalhos para Enviar, Aceitar e Rejeitar e para as telas |
| Animações de entrada/saída, fade, slide, parallax, animação ao rolar | Sem transições; a única animação é o indicador de progresso |
| Efeito de "levantar" no hover (translate + sombra) | Hover muda só o fundo para `SurfaceRaised` |
| Todo texto selecionável como em página web | Seleção só onde faz sentido copiar (`SelectableTextBlock` na transcrição, células da `DataGrid`) |
| Menu de contexto do navegador ou menus customizados em HTML | `ContextMenu` e `MenuFlyout` nativos do Avalonia |
| Seletor de arquivo e diálogos simulados em página | `StorageProvider` e janelas de diálogo nativas |
| Tabelas em `div` com rolagem da página | `DataGrid` com cabeçalho fixo, ordenação, redimensionamento de colunas e navegação por setas |
| Rolagem "elástica" e rolagem suave exagerada | Rolagem padrão do sistema |
| Clique duplo desnecessário ou confirmação "Tem certeza?" para tudo | Confirmação só para plano crítico (aprovação) e ação destrutiva |

---

## 7. Tipografia

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Inter (`Avalonia.Fonts.Inter`, `.WithInterFont()`) ou qualquer fonte da lista da seção 2 | Família **Barlow** (SIL OFL), embutida como `AvaloniaResource` |
| Carregar fonte do sistema ou da rede e deixar o visual mudar por máquina | As mesmas fontes em todos os sistemas operacionais |
| Algarismos proporcionais em tabelas e totais (colunas de números que "dançam") | `tnum` (algarismos tabulares) em tabelas, faixa de totais, quantidades, SKUs e endereços |
| Hierarquia por cor (títulos coloridos) | Hierarquia por largura, peso e tamanho |
| Título em caixa alta espaçada, estilo "eyebrow" de landing page | Título em caixa normal, Barlow Condensed |

Uso da família:

| Uso | Fonte |
|-----|-------|
| Títulos de tela e de quadro | Barlow Condensed SemiBold |
| Texto, rótulos, transcrição do Nexus | Barlow Regular / Medium |
| Tabelas (`DataGrid`), faixa de totais, quadro do plano | Barlow Semi Condensed, com `tnum` |

Conferido nos arquivos da `google/fonts` (`ofl/barlow`, `ofl/barlowcondensed`, `ofl/barlowsemicondensed`): as três larguras têm `tnum`, todos os acentos do português e licença SIL OFL 1.1.

---

## 8. Texto da interface

| Não fazer | Fazer no Nexus |
|-----------|----------------|
| Inglês misturado ("Dashboard", "Submit", "Settings") | Português em toda a interface (o código continua em inglês) |
| Termos técnicos expostos ao operador (JSON, `move_stock`, ID interno, stack trace) | Linguagem de negócio: "Mover 20 cx ABC-123 de DOCA-01 para A-03-02" |
| Mensagens de erro vagas ("Algo deu errado") | O que falhou, onde, e o que fazer: "Endereço A-03-02 bloqueado para inventário. Escolha outro endereço." |
| Pontuação exclamativa e tom de marketing | Frases curtas no indicativo |

---

## 9. Checklist de revisão de tela

Antes de considerar uma tela pronta:

- [ ] Nenhum `WebView`, HTML, CSS, recurso de rede ou lógica de navegador.
- [ ] Nenhum gradiente, sombra, blur, emoji, ✨ ou ilustração.
- [ ] Nenhum card, KPI card, badge em pílula, toast, FAB, modal com backdrop ou menu "⋯" escondendo ações.
- [ ] Cantos de 2 px em controles e 0 px em painéis.
- [ ] Só tokens de cor do `UI.md`; o vermelho da marca não aparece em estado nem em dado.
- [ ] Todo estado tem ícone + texto, não só cor.
- [ ] Fonte Barlow, com `tnum` em números e códigos.
- [ ] Toda ação funciona pelo teclado e tem foco visível.
- [ ] Sem animações, exceto o indicador de progresso.
- [ ] Textos em português, operacionais, sem termos técnicos.
