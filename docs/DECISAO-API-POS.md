# Desenho: API do PDV Mottainai

## Intenção

Adicionar uma API HTTP local em C# ao POS e atender exatamente as 24 rotas preenchidas pelo usuário. Cada terminal executará o serviço em loopback. Online, o PostgreSQL operacional `mottainai_operational`, schema `mottainai`, é a fonte transacional. Offline, o serviço usa cache e fila local protegidos; sincroniza operações pendentes quando a conexão voltar. O banco analítico permanece separado e não recebe operações do caixa.

## Estado encontrado

- O projeto atual é WPF em .NET 10, sem servidor HTTP. `MockApiService` guarda usuários, lojas, terminais e produtos em listas estáticas.
- O repositório `Mottainai-Banco-Operacional`, branch `develop`, já define `pos_terminal`, `pos_shift`, `pos_cash_movement`, `sales_transaction`, `sale_item`, `sale_payment`, `product`, `customer`, `app_user` e `staff_session`.
- O banco exige isolamento por empresa/loja via RLS. Cada requisição autenticada precisa usar transação, `SET LOCAL ROLE mottainai_api` e as funções de bootstrap documentadas. Não se deve definir manualmente os parâmetros de tenant.
- O contrato de identidade de funcionário usa Firebase UID; o banco não permite usar e-mail/CPF como lookup público após a migração de PII.
- O schema atual não possui autorização de sessão offline, nem estado de carrinho/rascunho em `sale_status`. Também não há rota de concluir venda na lista. Não registrar carrinho como venda concluída.
- O requisito operacional informado é: login normal online; login offline somente com autorização preparada; fechamento de caixa somente online; as demais rotas continuam disponíveis offline usando cache/fila. Offline, preços promocionais Mottainai não são aplicados; usar o preço normal sincronizado.

## Estado da implementação (2026-10-27)

O projeto ASP.NET, schema SQLite local, sessão offline protegida, cache apenas de preço normal, endpoints locais de turno/caixa/rascunho e contrato estático foram criados. Com `Firebase:ProjectId` e `ConnectionStrings:Operational`, o login valida o bearer Firebase, cria `staff_session` sob o contexto oficial/RLS, e prepara um grant local; terminais, catálogo, promoção vigente online e busca por identificador de cliente tokenizado consultam o banco operacional. O lookup de cliente usa uma função restrita que retorna só o ID, sem leitura direta de dados pessoais. O cache e as respostas offline usam somente o preço normal. A abertura e o fechamento de turno usam PostgreSQL quando online; o histórico e a consulta de vendas também leem vendas sincronizadas do PostgreSQL. A outbox transacional reaplica aberturas, movimentos, rascunhos/itens e rotação/revogação de sessões em ordem usando recibos idempotentes; o schema PostgreSQL e suas migrations versionadas ficam no repositório `Mottainai-Banco-Operacional`. Itens remotos ficam em estado `DRAFT` e não reservam lote/estoque. A API serializa aberturas concorrentes por terminal/funcionário; o banco já tem índices únicos que garantem no máximo um turno aberto por terminal e funcionário. A solução compila. Vendas e caixa passam pela fila local antes do replay. O fechamento recusa movimentos pendentes e inclui pagamentos em dinheiro já concluídos no banco. As migrations operacionais não foram aplicadas e os fluxos ainda não foram exercitados contra um ambiente Firebase/PostgreSQL autorizado. Não implantar como API operacional antes de aplicar/revisar as migrations e realizar essa validação.

## Abordagens consideradas

1. **Hospedar endpoints dentro do WPF:** descartada porque o aplicativo desktop não deve ser o servidor compartilhado do banco e não fornece um serviço HTTP disponível aos terminais.
2. **Adicionar ASP.NET Core ao mesmo projeto WPF:** descartada porque acopla hospedagem de API à interface desktop e dificulta implantar a API sem abrir o aplicativo.
3. **Criar um projeto ASP.NET Core local independente na mesma solução/repositório:** escolhida. Mantém a API implantável separadamente por terminal, disponível em loopback durante a perda de internet, e reutiliza o ambiente .NET 10. Usar Npgsql com SQL parametrizado e transações explícitas para obedecer ao RLS, às funções do banco e às chaves compostas das tabelas particionadas. SQLite local conserva cache e fila offline; quando a API consegue acessar PostgreSQL, grava nele e confirma a fila.

## Escopo

Criar `PDVMottainai.Api` ao lado do aplicativo WPF, com bind apenas a loopback por padrão, configuração de conexão por variável de ambiente/secret manager, rotas organizadas por domínio, validação de DTOs, respostas HTTP consistentes, contrato OpenAPI versionado como arquivo estático no repositório (sem rota adicional) e os seguintes endpoints, sem rota 25:

| # | Método e rota | Persistência/uso |
|---:|---|---|
| 1 | `POST /auth/login` | Validar identidade de funcionário conforme o contrato Firebase do banco; criar sessão em `staff_session`. |
| 2 | `POST /auth/refresh` | Rotacionar sessão e hash do refresh token. |
| 3 | `POST /auth/logout` | Revogar a sessão atual. |
| 4 | `GET /auth/me` | Retornar operador, loja e permissões derivadas do contexto autenticado. |
| 5 | `POST /auth/offline-login` | Validar autorização offline previamente emitida e limitada ao terminal/operador. |
| 6 | `POST /offline-sessions` | Emitir autorização curta para operação offline. |
| 7 | `GET /terminals` | Listar terminais autorizados online ou no cache autorizado. |
| 8 | `GET /capabilities` | Retornar capacidades por versão/API, papel e modo online/offline. |
| 9 | `POST /shifts` | Abrir turno no PostgreSQL ou localmente com sincronização posterior. |
| 10 | `GET /shifts/current` | Consultar turno aberto online ou local. |
| 11 | `GET /shifts/{shiftId}` | Consultar turno online ou local dentro do tenant. |
| 12 | `GET /shifts/{shiftId}/summary` | Calcular saldo esperado e totais do turno online ou local. |
| 13 | `POST /shifts/{shiftId}/closing` | Somente online: fechar turno e salvar valores contados/esperados. |
| 14 | `GET /shifts/{shiftId}/cash-movements` | Listar movimentos sincronizados e pendentes. |
| 15 | `POST /shifts/{shiftId}/cash-withdrawals` | Registrar sangria online ou na fila local idempotente. |
| 16 | `POST /shifts/{shiftId}/cash-deposits` | Registrar suprimento online ou na fila local idempotente. |
| 17 | `GET /products` | Buscar catálogo e preço vigente; offline usa somente preço normal em cache, sem promoção Mottainai. |
| 18 | `GET /products/{productId}` | Consultar produto online ou no cache autorizado, com preço normal offline. |
| 19 | `POST /customers/search` | Buscar conta por identificador permitido; offline usa somente índice tokenizado/cache, sem CPF ou contato em claro. |
| 20 | `POST /sales` | Iniciar carrinho no PostgreSQL ou fila local com chave idempotente. |
| 21 | `GET /sales` | Histórico sincronizado e vendas locais pendentes. |
| 22 | `GET /sales/{saleId}` | Consultar venda/carrinho online ou local, preservando a data da chave particionada. |
| 23 | `POST /sales/{saleId}/items` | Incluir produto online ou na fila; preço offline é o preço normal em cache. |
| 24 | `PATCH /sales/{saleId}/items/{itemId}` | Alterar quantidade online ou localmente com controle de versão. |

## Segurança e dados

- Usar apenas o banco operacional em comandos de PDV. Todas as leituras e gravações protegidas executam em transação e sob o papel `mottainai_api`; o bootstrap do banco estabelece o tenant e o RLS restringe cada consulta.
- Validar a identidade do operador pelo fluxo Firebase documentado. A API nunca aceita `employeeId`, `storeId` ou `companyId` do cliente como autoridade.
- Guardar somente hash SHA-256 de refresh tokens em `staff_session`; devolver segredos apenas uma vez; nunca registrar senha, token, CPF ou payload pessoal nos logs.
- Toda escrita de venda/caixa usa idempotência e transação. Offline, a fila registra chave, corpo protegido, identidade do terminal e ordem; ao reconectar, conflitos ficam pendentes de resolução e não são descartados. Responder conflito quando a chave de idempotência reutilizada tiver payload diferente.
- Não modificar, instalar ou migrar o banco real automaticamente. Scripts incrementais necessários ficam versionados e exigem aplicação controlada pelo operador do banco.
- Credenciais e Firebase config ficam fora do Git em variáveis de ambiente/secret manager. Não incluir `.env` com segredos.

## Dependências de schema e limite funcional

O banco local precisa de cache de catálogo/preço normal, terminal e sessão, além de fila de turno/caixa/carrinho. Login normal e fechamento do turno exigem PostgreSQL online. `POST /offline-sessions` prepara credencial local enquanto online; `POST /auth/offline-login` valida essa autorização local sem rede. As outras rotas funcionam contra o cache/fila quando offline. Na volta da rede, a fila sincroniza em ordem com idempotência e preserva conflitos para resolução. Promoções Mottainai não entram no cache offline e o preço aplicado offline é o preço normal sincronizado.

O esquema operacional precisa de migração aditiva para `DRAFT` em `sale_status` e para uma chave idempotente de venda, além dos privilégios/RLS necessários. O fluxo de venda listado termina na edição de itens; sem endpoint de pagamento/conclusão, esta entrega não marcará um carrinho como venda concluída, não gravará pagamento/fiscal e não baixará estoque como se o checkout tivesse ocorrido. O histórico `/sales` distinguirá vendas concluídas de carrinhos e operações pendentes locais.

## Fora de escopo

- Adicionar endpoint 25 ou alterar a lista de 24 rotas.
- Alterar a interface WPF para consumir a API nesta etapa.
- Gravar transações no banco analítico.
- Emitir documento fiscal, processar adquirente/pagamento ou dar baixa final de estoque sem uma rota de checkout definida.
- Aplicar migrações no banco remoto/real.

## Critérios de aceite do desenho

- As 24 rotas estão contempladas e a linha 25 permanece vazia.
- Online, o banco operacional é a fonte transacional; offline, as mutações ficam em fila local e sincronizam quando reconectar. O banco analítico não participa do fluxo do PDV.
- O contexto de tenant é estabelecido pelas funções oficiais do PostgreSQL e todo acesso respeita RLS.
- O contrato de login respeita o uso de Firebase UID previsto pelo banco.
- Login normal e fechamento são bloqueados sem rede; autorização/login offline e demais rotas usam armazenamento local persistente, sem promoções e com preços normais.
- Autorizações offline e carrinhos não são simulados em memória nem confundidos com vendas concluídas.
- O projeto API pode ser iniciado sem iniciar o aplicativo WPF e sem credenciais versionadas.
