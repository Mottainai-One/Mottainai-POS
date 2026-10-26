# Mottainai POS

Aplicação desktop Windows em WPF para representar um ponto de venda do ecossistema Mottainai. A solução contém uma interface de login e uma tela de venda, modelos de dados e um serviço chamado MockApiService com dados de demonstração.

## Estado atual

O projeto é um protótipo demonstrativo. A fonte versionada não contém uma implementação de integração HTTP com a API, configuração de banco de dados ou instruções de implantação. Não use os dados simulados como registros reais de venda.

## Tecnologias e requisitos

- C# e WPF sobre .NET 10 para Windows, conforme PDVMottainai/PDVMottainai.csproj.
- Visual Studio com o workload de desenvolvimento desktop .NET ou .NET SDK compatível.
- Windows para executar a interface WPF.

## Compilar

No Windows, a partir da raiz do repositório:

    dotnet build .\PDVMottainai.slnx

Para executar o projeto diretamente:

    dotnet run --project .\PDVMottainai\PDVMottainai.csproj

## Estrutura

- PDVMottainai/Views: telas WPF.
- PDVMottainai/Models: modelos de usuário, loja, produto, venda e operações de caixa.
- PDVMottainai/Services/MockApiService.cs: dados e comportamento de demonstração.
- PDVMottainai/IMG: recursos visuais.

## Licença

Consulte [LICENSE](LICENSE).
