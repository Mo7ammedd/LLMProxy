# syntax=docker/dockerfile:1@sha256:ecfaec9ed6d810b56388c508f4121597bfbba70d41a6dfeee4d8cad5f295fc32
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/LLMProxy.Server/LLMProxy.Server.csproj --locked-mode
RUN dotnet publish src/LLMProxy.Server/LLMProxy.Server.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false
RUN mkdir /gateway-data && chown 1654:1654 /gateway-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=4000 \
    DOTNET_EnableDiagnostics=0 \
    LLMProxy__Storage__SqlitePath=/data/llmproxy.db
COPY --from=build --chown=1654:1654 /out/ ./
COPY --from=build --chown=1654:1654 /gateway-data /data
COPY --from=build /source/LICENSE ./LICENSE
LABEL org.opencontainers.image.title="LLMProxy" \
      org.opencontainers.image.description="OpenAI-compatible LLM gateway for .NET" \
      org.opencontainers.image.source="https://github.com/Mo7ammedd/LLMProxy" \
      org.opencontainers.image.licenses="MIT"
USER 1654:1654
EXPOSE 4000
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
    CMD ["dotnet", "LLMProxy.Server.dll", "healthcheck"]
STOPSIGNAL SIGTERM
ENTRYPOINT ["dotnet", "LLMProxy.Server.dll"]
