FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:20387c6674c30e46def0cc8cb557bd2a69b35afdf18c19c3694638d0a90897e4 AS dotnet
FROM jenkins/ssh-agent:8.4.0-jdk21@sha256:3ec6d602192b1d2add1683b7298c6a1effeb4063ea0431d369d66c17cf1f2325
USER root
RUN apt-get update && apt-get install -y --no-install-recommends python3 nodejs curl libicu-dev libssl-dev libsqlite3-0 docker-cli && rm -rf /var/lib/apt/lists/*
RUN apt-get update && apt-get install -y --no-install-recommends docker-buildx && rm -rf /var/lib/apt/lists/*
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet && curl -fsSL https://dl.k8s.io/release/v1.34.1/bin/linux/amd64/kubectl -o /usr/local/bin/kubectl && curl -fsSL https://dl.k8s.io/release/v1.34.1/bin/linux/amd64/kubectl.sha256 -o /tmp/kubectl.sha256 && echo "$(cat /tmp/kubectl.sha256)  /usr/local/bin/kubectl" | sha256sum -c - && chmod +x /usr/local/bin/kubectl
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GCHeapHardLimit=0x40000000 DOTNET_gcServer=0 KUBECONFIG=/run/ci/kubeconfig CI_AGENT_CONTAINER=burmalda-ci-agent
# Local educational agent: the host Docker socket already grants host-level Docker access.
COPY docker/jenkins/agent.sh /usr/local/bin/burmalda-agent
RUN sed -i 's/\r$//' /usr/local/bin/burmalda-agent && chmod +x /usr/local/bin/burmalda-agent
ENTRYPOINT ["/usr/local/bin/burmalda-agent"]
