FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=WS01-01
WORKDIR /source
COPY Directory.Build.props ./
COPY src/ ./src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -r linux-x64 \
    --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None \
    -p:DebugSymbols=false -p:EnableCompressionInSingleFile=true \
    -m:1 -o /publish \
    && test "$(find /publish -maxdepth 1 -type f | wc -l)" -eq 1 \
    && mv /publish/${PROJECT} /publish/service

# Native OS dependencies only: no installed dotnet or ASP.NET runtime.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0
WORKDIR /app
COPY --from=build --chown=app:app /publish/service ./service
ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/dotnet-bundle \
    Logging__LogLevel__Default=Information \
    Logging__LogLevel__Microsoft.AspNetCore=Warning
USER app
EXPOSE 8080
ENTRYPOINT ["./service"]
