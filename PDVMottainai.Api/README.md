# API local do PDV Mottainai

Projeto ASP.NET Core independente da interface desktop, que fica no repositório [Mottainai-POS_front](https://github.com/Mottainai-One/mottainai-POS_front). Base URL padrão: `http://127.0.0.1:5097`. Execute `dotnet run --project PDVMottainai.Api` no Windows. A API escuta loopback. A especificação está em `docs/API-POS.openapi.yaml`; ela não é servida por uma rota HTTP.

Configuração é feita por variáveis de ambiente. `Api__Port` define a porta; `Api__TerminalId` é obrigatório para login offline vinculado ao terminal. `Firebase__ProjectId` e `ConnectionStrings__Operational` configuram o Firebase Auth e PostgreSQL operacional; nunca use a conexão analítica. Credenciais ficam em secret store/ambiente protegido, nunca em appsettings versionado. O login e a preparação do grant usam Firebase/PostgreSQL quando configurados.

O SQLite local persiste catálogo com preços normais, sessão local, turnos, movimentos, rascunhos, pedidos de aprovação, notificações pendentes de movimentos de caixa e fila de sincronização. Promoções Mottainai não fazem parte do cache offline. Para usar uma promoção online, o operador primeiro valida a conta em `POST /customers/search`, e o produto/venda deve receber o `customerId` validado dentro da janela local de 15 minutos; sem validação, aplica-se o preço normal. Ao adicionar o item ao carrinho, a resposta também retorna `normalPrice`, `promotionalPrice` (nulo quando não há promoção), `promotionId` e `unitPrice` (preço aplicado). Offline, `unitPrice` usa somente o preço normal do cache. A abertura e o fechamento online usam PostgreSQL; sem conexão, o fechamento falha e mantém o turno aberto. Antes de habilitar as gravações/replay, aplique no banco operacional as atualizações `update_existing_api_rpa.sql` e `update_existing_pos_api.sql` do repositório [Mottainai-Banco-Operacional](https://github.com/Mottainai-One/Mottainai-Banco-Operacional); não use o banco analítico. Os itens sincronizados permanecem `DRAFT` e não reservam lote/estoque. O fechamento recusa movimentos pendentes e calcula pagamentos em dinheiro já concluídos no PostgreSQL. Refresh/logout do `staff_session` também é enfileirado para reconciliação.

## Cash movement notifications

Cash deposits and withdrawals are stored in a protected local outbox. After a movement syncs with PostgreSQL, the API sends its type, amount, reason, store, and timestamp through the Firebase bridge. The bridge publishes it to the Redis inbox and sends a push notification to active managers. Failed deliveries remain pending for retry. This bridge is retained for cash movement alerts; approval requests no longer use it.

## Finalização simulada

`POST /sales/{saleId}/completion-simulation` retorna o total e a quantidade de itens como `SIMULATED`. A rota não registra pagamento, não altera o banco operacional e não transforma o rascunho em venda concluída.

## Manager approvals

`POST /approvals` creates a `CANCEL_PURCHASE`, `CANCEL_PRODUCT`, `REFUND`, `RETURN`, or `SPECIAL_DISCOUNT` request and can queue it while offline. The outbox syncs requests with `mottainai_operational`. `GET /approvals/{requestId}` returns the local request state.

For an online decision, the manager signs in with Firebase Authentication in the POS interface. The client sends the resulting Firebase ID token to `POST /approvals/{requestId}/manager-decision` with `{ "approved": true, "observation": "..." }`. The API verifies the token and confirms that the account is an active manager in the request's store before recording the decision. Never send or store the manager's password in the POS API.

Manager decisions require internet access. If the POS is offline, the interface must ask the cashier to call a manager; the API cannot verify a password or apply the decision offline. Cancellation requests execute through `pos_cancel_request`. Approved special discounts update an item while its sale is still `DRAFT`. Refund and return requests are recorded as approved or rejected; no payment processor or inventory-return integration is included, and no payment is processed.

The Firebase bridge remains configured with `Firebase__PosApprovalFunctionUrl` and `Firebase__PosApprovalApiSecret` for cash movement notifications only. Keep the shared secrets on the API and bridge environments, never in the app or version control.
