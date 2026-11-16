FROM mcr.microsoft.com/dotnet/sdk:9.0@sha256:20387c6674c30e46def0cc8cb557bd2a69b35afdf18c19c3694638d0a90897e4 AS dotnet
FROM jenkins/ssh-agent:8.4.0-jdk21@sha256:3ec6d602192b1d2add1683b7298c6a1effeb4063ea0431d369d66c17cf1f2325 AS tools
USER root
ARG DOWNLOAD_PROXY=
RUN if [ -n "$DOWNLOAD_PROXY" ]; then sed -i "s|http://deb.debian.org|$DOWNLOAD_PROXY/deb.debian.org|g" /etc/apt/sources.list.d/debian.sources; fi && apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=180 update && apt-get -o Acquire::Retries=2 -o Acquire::http::Timeout=180 install -y --no-install-recommends python3 nodejs curl git libicu76 libsqlite3-0 docker-cli docker-buildx && rm -rf /var/lib/apt/lists/* && sed -i "s|${DOWNLOAD_PROXY:-https://deb.debian.org}/deb.debian.org|https://deb.debian.org|g" /etc/apt/sources.list.d/debian.sources
COPY --from=dotnet /usr/share/dotnet /usr/share/dotnet
FROM tools AS agent
RUN ln -s /usr/share/dotnet/dotnet /usr/local/bin/dotnet && git config --global --add safe.directory /opt/ci/git-cache.git && curl -fsSL "${DOWNLOAD_PROXY:-https:/}/dl.k8s.io/v1.34.1/kubernetes-client-linux-amd64.tar.gz" -o /tmp/kubernetes-client.tar.gz && tar -xzf /tmp/kubernetes-client.tar.gz -C /tmp kubernetes/client/bin/kubectl && mv /tmp/kubernetes/client/bin/kubectl /usr/local/bin/kubectl && curl -fsSL "${DOWNLOAD_PROXY:-https:/}/dl.k8s.io/release/v1.34.1/bin/linux/amd64/kubectl.sha256" -o /tmp/kubectl.sha256 && echo "$(cat /tmp/kubectl.sha256)  /usr/local/bin/kubectl" | sha256sum -c - && chmod +x /usr/local/bin/kubectl && rm -rf /tmp/kubernetes /tmp/kubernetes-client.tar.gz /tmp/kubectl.sha256
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_GCHeapHardLimit=0x40000000 DOTNET_gcServer=0 KUBECONFIG=/ci-secrets/kubeconfig DOCKER_HOST=unix:///ci-docker.sock CI_AGENT_CONTAINER=burmalda-ci-agent
# Local educational agent: the host Docker socket already grants host-level Docker access.
COPY docker/jenkins/agent.sh /usr/local/bin/burmalda-agent
RUN sed -i 's/\r$//' /usr/local/bin/burmalda-agent && chmod +x /usr/local/bin/burmalda-agent
ENTRYPOINT ["/usr/local/bin/burmalda-agent"]
