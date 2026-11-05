FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PDVMottainai.Api/PDVMottainai.Api.csproj PDVMottainai.Api/
RUN dotnet restore PDVMottainai.Api/PDVMottainai.Api.csproj
COPY PDVMottainai.Api/ PDVMottainai.Api/
WORKDIR /src/PDVMottainai.Api
RUN dotnet publish PDVMottainai.Api.csproj --configuration Release --output /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish/ .
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 10000
ENTRYPOINT ["dotnet", "PDVMottainai.Api.dll"]
