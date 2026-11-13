# Pin the .NET 9 SDK/runtime manifests. global.json permits SDK 9.0.3xx patches.
FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:20387c6674c30e46def0cc8cb557bd2a69b35afdf18c19c3694638d0a90897e4 AS publish
WORKDIR /src
ARG PROJECT_PATH
ARG NUGET_AUDIT=true
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/ src/
RUN --mount=type=cache,id=burmalda-nuget,target=/root/.nuget/packages,sharing=locked --mount=type=cache,id=burmalda-nuget-http,target=/root/.local/share/NuGet/http-cache,sharing=locked dotnet restore "$PROJECT_PATH" -m:1 -p:NuGetAudit=$NUGET_AUDIT
RUN --mount=type=cache,id=burmalda-nuget,target=/root/.nuget/packages,sharing=locked dotnet publish "$PROJECT_PATH" -c Release --no-restore -o /published -m:1 -p:UseSharedCompilation=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0@sha256:779b0b298964fe93e6c6388c8bb823393a7c301b79cd5b3d80632bc886b2ffd4 AS runtime
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
ARG ASSEMBLY_NAME
ENV APP_ASSEMBLY=${ASSEMBLY_NAME} ASPNETCORE_HTTP_PORTS=8080
COPY --from=publish /published/ ./
RUN ln -s "/app/${ASSEMBLY_NAME}" /app/service
EXPOSE 8080
ENTRYPOINT ["/app/service"]

FROM nginx:1.28-alpine AS web
COPY --from=publish /published/wwwroot/ /usr/share/nginx/html/
COPY docker/web/default.conf /etc/nginx/conf.d/default.conf
ARG LEARNING_LABS=false
ENV LEARNING_LABS=${LEARNING_LABS}
COPY docker/web/20-appsettings.sh /docker-entrypoint.d/20-appsettings.sh
RUN sed -i 's/\r$//' /docker-entrypoint.d/20-appsettings.sh && chmod +x /docker-entrypoint.d/20-appsettings.sh
EXPOSE 8080
