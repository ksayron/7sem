FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
ARG PROJECT_PATH
COPY Directory.Build.props Directory.Packages.props global.json Burmalda.slnx ./
COPY src/BuildingBlocks/Burmalda.Contracts/Burmalda.Contracts.csproj src/BuildingBlocks/Burmalda.Contracts/
COPY src/BuildingBlocks/Burmalda.ServiceDefaults/Burmalda.ServiceDefaults.csproj src/BuildingBlocks/Burmalda.ServiceDefaults/
COPY src/Gateway/Burmalda.Gateway/Burmalda.Gateway.csproj src/Gateway/Burmalda.Gateway/
COPY src/Services/Identity/Burmalda.Identity.Api/Burmalda.Identity.Api.csproj src/Services/Identity/Burmalda.Identity.Api/
COPY src/Services/Catalog/Burmalda.Catalog.Api/Burmalda.Catalog.Api.csproj src/Services/Catalog/Burmalda.Catalog.Api/
COPY src/Services/Lending/Burmalda.Lending.Api/Burmalda.Lending.Api.csproj src/Services/Lending/Burmalda.Lending.Api/
COPY src/Services/Economy/Burmalda.Economy.Api/Burmalda.Economy.Api.csproj src/Services/Economy/Burmalda.Economy.Api/
COPY src/Services/Reputation/Burmalda.Reputation.Api/Burmalda.Reputation.Api.csproj src/Services/Reputation/Burmalda.Reputation.Api/
COPY src/Services/Notifications/Burmalda.Notifications.Api/Burmalda.Notifications.Api.csproj src/Services/Notifications/Burmalda.Notifications.Api/
RUN dotnet restore "$PROJECT_PATH" -m:1
COPY . .
RUN dotnet publish "$PROJECT_PATH" -c Release --no-restore -o /app -m:1

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
ARG ASSEMBLY_NAME
ENV APP_ASSEMBLY=${ASSEMBLY_NAME}
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "dotnet ${APP_ASSEMBLY}.dll"]
