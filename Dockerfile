# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
ARG TARGETARCH
WORKDIR /source
COPY global.json PaperlessLlm.slnx ./
COPY src/PaperlessLlm/PaperlessLlm.csproj src/PaperlessLlm/packages.lock.json src/PaperlessLlm/
RUN dotnet restore src/PaperlessLlm/PaperlessLlm.csproj --locked-mode
COPY src/PaperlessLlm/ src/PaperlessLlm/
RUN dotnet publish src/PaperlessLlm/PaperlessLlm.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:10.0.12-noble@sha256:ff17a18b639a0327e52c7c296fa2e1abe6e03eb61d8121a8ef67cc6aa430a27e AS runtime
ARG VERSION=0.1.0
ARG REVISION=unknown
LABEL org.opencontainers.image.source="https://github.com/mggarofalo/paperless-llm" \
      org.opencontainers.image.description="Read-only Paperless OCR and metadata review worker" \
      org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$REVISION
USER root
RUN apt-get update && apt-get install -y --no-install-recommends poppler-utils ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/auth /data/state /data/audit \
    && chown -R app:app /data && chmod 700 /data/auth /data/state /data/audit
WORKDIR /app
COPY --from=build /out/ ./
ENV DOTNET_EnableDiagnostics=0 \
    PPLLM_AUTH_DIRECTORY=/data/auth \
    PPLLM_STATE_DIRECTORY=/data/state \
    PPLLM_AUDIT_DIRECTORY=/data/audit
USER app
ENTRYPOINT ["dotnet", "PaperlessLlm.dll"]
CMD ["worker"]
