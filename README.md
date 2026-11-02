# Mottainai POS — Back-end

API local do ponto de venda Mottainai, desenvolvida com ASP.NET Core e .NET 10. O serviço oferece contratos HTTP para sessões de operadores, consulta de produtos, turnos, movimentações de caixa, vendas em rascunho e pedidos de aprovação. O aplicativo desktop é mantido no [Mottainai POS Front-end](https://github.com/Mottainai-One/mottainai-POS_front).

## Funcionalidades

- Autenticação online, sessões locais, renovação, saída e preparação de acesso offline vinculado ao terminal.
- Consulta de capacidades, terminais, produtos e clientes.
- Abertura e consulta de turnos, resumo de caixa, sangria, suprimento e fechamento.
- Criação e consulta de vendas em rascunho, inclusão de itens e alteração de quantidades.
- Pedidos de aprovação para cancelamento, estorno, devolução e desconto especial.
- Armazenamento local em SQLite e fila de sincronização com o PostgreSQL operacional.
- Encaminhamento de pedidos de aprovação e avisos de movimentos de caixa por uma ponte configurável.

## Arquitetura

```text
Aplicação cliente → API local (loopback) → SQLite / fila local
                              ├──────────→ PostgreSQL operacional
                              └──────────→ Firebase / ponte de notificações
```

A API usa autenticação Bearer para rotas protegidas. O acesso ao PostgreSQL é feito no contexto da requisição e das regras do banco operacional. A fila local usa chaves de idempotência para evitar reaplicar a mesma operação com conteúdo diferente. Os segredos devem ser fornecidos por ambiente seguro.

## Rotas

O endereço padrão é `http://127.0.0.1:5097`. A referência versionada está em [docs/API-POS.openapi.yaml](docs/API-POS.openapi.yaml); o arquivo não é servido automaticamente por uma rota HTTP.

| Área | Rotas |
|---|---|
| Sessões | `POST /auth/login`, `POST /auth/offline-login`, `POST /auth/refresh`, `POST /auth/logout`, `GET /auth/me`, `POST /offline-sessions` |
| Referência | `GET /terminals`, `GET /capabilities`, `GET /products`, `GET /products/{productId}`, `POST /customers/search` |
| Turnos | `POST /shifts`, `GET /shifts/current`, `GET /shifts/{shiftId}`, `GET /shifts/{shiftId}/summary`, `POST /shifts/{shiftId}/closing` |
| Caixa | `GET /shifts/{shiftId}/cash-movements`, `POST /shifts/{shiftId}/cash-withdrawals`, `POST /shifts/{shiftId}/cash-deposits` |
| Vendas | `GET /sales`, `POST /sales`, `GET /sales/{saleId}`, `POST /sales/{saleId}/items`, `PATCH /sales/{saleId}/items/{itemId}`, `POST /sales/{saleId}/completion-simulation` |
| Aprovações | `POST /approvals`, `GET /approvals/{requestId}` |

## Operação e regras importantes

- A preparação do acesso offline e o fechamento do turno exigem conexão; sem conexão no fechamento, o turno permanece aberto.
- O cache offline usa preços normais. A aplicação de promoção online exige validação prévia do cliente; consulte o [guia da API](PDVMottainai.Api/README.md) para a janela de validação e os campos retornados.
- Os itens sincronizados permanecem em `DRAFT`. A rota `completion-simulation` calcula o resultado de uma simulação; ela não registra pagamento nem conclui uma venda.
- Pedidos de aprovação usam os tipos `CANCEL_PURCHASE`, `CANCEL_PRODUCT`, `REFUND`, `RETURN` e `SPECIAL_DISCOUNT`. Para estornos e devoluções, o registro de aprovação não representa execução financeira ou devolução de estoque.
- A sincronização e as notificações dependem da configuração e disponibilidade dos serviços externos. Os detalhes da ponte e das alterações de banco necessárias estão no [guia da API](PDVMottainai.Api/README.md) e em [Database/README.md](PDVMottainai.Api/Database/README.md).

## Configuração

| Variável de ambiente | Finalidade |
|---|---|
| `Api__Port` | Porta local da API; padrão `5097`. |
| `Api__TerminalId` | Identificação do terminal para acesso offline. |
| `LocalStore__Path` | Caminho do banco SQLite local. |
| `Firebase__ProjectId` | Projeto Firebase usado na autenticação. |
| `ConnectionStrings__Operational` | Conexão com o PostgreSQL operacional. |
| `Firebase__PosApprovalFunctionUrl` | Endereço HTTPS da ponte de aprovação e notificações. |
| `Firebase__PosApprovalApiSecret` | Segredo de autenticação da ponte. |

Os parâmetros da ponte e suas dependências são descritos no [guia da API](PDVMottainai.Api/README.md). Não coloque tokens, senhas ou strings de conexão em arquivos versionados. Use o banco operacional, não o analítico, para as operações do PDV.

## Requisitos e execução

Instale Windows e o SDK .NET 10. Configure as variáveis necessárias para os serviços que deseja utilizar e, na raiz do repositório, execute:

```powershell
dotnet restore PDVMottainai.slnx
dotnet build PDVMottainai.slnx
dotnet run --project PDVMottainai.Api
```

O serviço escuta apenas em loopback; o caminho padrão do SQLite fica em `%LOCALAPPDATA%/Mottainai/PDV/pos-local.db`. O workflow [`.NET Desktop CI`](.github/workflows/dotnet-desktop.yml) restaura e compila a solução em Debug e Release no Windows.

## Estrutura

```text
.
├── PDVMottainai.slnx
├── PDVMottainai.Api/
│   ├── Endpoints/          # controllers HTTP
│   ├── Data/               # armazenamento local e acesso PostgreSQL
│   ├── Database/Local/     # estrutura SQLite
│   ├── Security/           # autenticação das sessões
│   ├── Services/           # sincronização e ponte de notificações
│   └── Program.cs          # configuração e inicialização
├── docs/                   # OpenAPI e decisões da API
└── LICENSE
```

Consulte também o [README técnico da API](PDVMottainai.Api/README.md), a [documentação do banco](PDVMottainai.Api/Database/README.md) e a [licença](LICENSE).
