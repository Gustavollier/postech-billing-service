# Build e publicação em estágios: a imagem final não carrega o SDK.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Os .csproj primeiro, sozinhos: o restore vira camada de cache e só refaz
# quando uma dependência muda, não a cada alteração de código.
COPY src/PosTech.Contratos/*.csproj src/PosTech.Contratos/
COPY src/PosTech.Mensageria/*.csproj src/PosTech.Mensageria/
COPY src/PosTech.Billing/*.csproj src/PosTech.Billing/
COPY src/PosTech.Billing.Api/*.csproj src/PosTech.Billing.Api/
RUN dotnet restore src/PosTech.Billing.Api/PosTech.Billing.Api.csproj

COPY src/ src/
RUN dotnet publish src/PosTech.Billing.Api/PosTech.Billing.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Usuário sem privilégio. A imagem do .NET já traz o `app`; rodar como root
# dentro do cluster é risco sem contrapartida.
USER app

COPY --from=build --chown=app:app /app .

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080

ENTRYPOINT ["dotnet", "PosTech.Billing.Api.dll"]
