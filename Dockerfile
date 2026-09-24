# syntax=docker/dockerfile:1
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY . .
RUN dotnet restore src/LLMProxy.Server/LLMProxy.Server.csproj --locked-mode
RUN dotnet publish src/LLMProxy.Server/LLMProxy.Server.csproj \
    --configuration Release --no-restore --output /out /p:UseAppHost=false
RUN mkdir /gateway-data && chown 1654:1654 /gateway-data

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
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
