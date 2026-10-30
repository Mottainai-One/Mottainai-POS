# API local do PDV Mottainai

Projeto ASP.NET Core independente do WPF. Base URL padrão: `http://127.0.0.1:5097`. Execute `dotnet run --project PDVMottainai.Api` no Windows. A API escuta loopback. A especificação está em `docs/API-POS.openapi.yaml`; ela não é servida por uma rota HTTP.

Configuração é feita por variáveis de ambiente. `Api__Port` define a porta; `Api__TerminalId` é obrigatório para login offline vinculado ao terminal. `Firebase__ProjectId` e `ConnectionStrings__Operational` configuram o Firebase Auth e PostgreSQL operacional; nunca use a conexão analítica. Credenciais ficam em secret store/ambiente protegido, nunca em appsettings versionado. O login e a preparação do grant usam Firebase/PostgreSQL quando configurados.

O SQLite local persiste catálogo com preços normais, sessão local, turnos, movimentos, rascunhos e fila de sincronização. Promoções Mottainai não fazem parte do cache offline; online, catálogo e inclusão de item podem usar a promoção vigente elegível. A abertura e o fechamento online usam PostgreSQL; sem conexão, o fechamento falha e mantém o turno aberto. Antes de habilitar as gravações/replay, aplique no banco operacional as atualizações `update_existing_api_rpa.sql` e `update_existing_pos_api.sql` do repositório [Mottainai-Banco-Operacional](https://github.com/Mottainai-One/Mottainai-Banco-Operacional); não use o banco analítico. Os itens sincronizados permanecem `DRAFT` e não reservam lote/estoque. O fechamento recusa movimentos pendentes e calcula pagamentos em dinheiro já concluídos no PostgreSQL. Refresh/logout do `staff_session` também é enfileirado para reconciliação.

O conjunto deste projeto registra as 24 rotas solicitadas; a linha 25 permanece vazia. Não existe rota adicional de health ou OpenAPI.
