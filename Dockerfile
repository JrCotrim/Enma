# syntax=docker/dockerfile:1

# ENMA deployment images (D3, D13, D14). Build from the repository root:
#   docker build --target api      --build-arg REVISION=<git-sha> -t enma-api:<tag> .
#   docker build --target migrator --build-arg REVISION=<git-sha> -t enma-migrator:<tag> .
# No secret is passed to, or stored by, any stage: configuration arrives only
# at run time through the environment.

FROM node:24.18.0-bookworm-slim AS web
ENV npm_config_update_notifier=false \
    npm_config_fund=false \
    npm_config_audit=false
WORKDIR /src/Enma.Web
COPY src/Enma.Web/package.json src/Enma.Web/package-lock.json ./
RUN npm ci
COPY src/Enma.Web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1
WORKDIR /src
COPY .config/dotnet-tools.json .config/
COPY src/Enma.Domain/Enma.Domain.csproj src/Enma.Domain/
COPY src/Enma.Application/Enma.Application.csproj src/Enma.Application/
COPY src/Enma.Infrastructure/Enma.Infrastructure.csproj src/Enma.Infrastructure/
COPY src/Enma.Api/Enma.Api.csproj src/Enma.Api/
RUN dotnet restore src/Enma.Api/Enma.Api.csproj
COPY src/Enma.Domain/ src/Enma.Domain/
COPY src/Enma.Application/ src/Enma.Application/
COPY src/Enma.Infrastructure/ src/Enma.Infrastructure/
COPY src/Enma.Api/ src/Enma.Api/
RUN dotnet publish src/Enma.Api/Enma.Api.csproj \
        --configuration Release \
        --no-restore \
        --output /out/api \
        -p:UseAppHost=false

# D14: self-contained linux-x64 migrations bundle. It reads the connection
# string through the design-time factory (ENMA_DESIGNTIME_CONNECTION_STRING).
FROM build AS bundle
RUN dotnet tool restore \
    && dotnet tool run dotnet-ef migrations bundle \
        --project src/Enma.Infrastructure/Enma.Infrastructure.csproj \
        --startup-project src/Enma.Infrastructure/Enma.Infrastructure.csproj \
        --context EnmaDbContext \
        --configuration Release \
        --self-contained \
        --target-runtime linux-x64 \
        --output /out/migrator/efbundle

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0.10 AS migrator
ARG REVISION=unknown
LABEL org.opencontainers.image.title="enma-migrator" \
      org.opencontainers.image.description="ENMA EF Core migrations bundle (run once, with the API stopped)" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${REVISION}" \
      org.opencontainers.image.licenses="NOASSERTION"
WORKDIR /migrator
COPY --from=bundle --chmod=0555 /out/migrator/efbundle ./efbundle
USER app
# Fails closed instead of falling back to the design-time local default.
ENTRYPOINT ["/bin/sh", "-ec", "if [ -z \"${ENMA_DESIGNTIME_CONNECTION_STRING:-}\" ]; then echo 'ENMA_DESIGNTIME_CONNECTION_STRING is required.' >&2; exit 64; fi; exec /migrator/efbundle \"$@\"", "efbundle"]

# D13: official non-chiseled ASP.NET Core runtime (tzdata + ICU), non-root
# user `app`, plain HTTP 8080 reachable only on the Compose network; TLS ends
# at the edge.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10 AS api
ARG REVISION=unknown
LABEL org.opencontainers.image.title="enma-api" \
      org.opencontainers.image.description="ENMA API serving the Enma.Web build on the same origin" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.version="${REVISION}" \
      org.opencontainers.image.licenses="NOASSERTION"
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    DataProtection__KeysPath=/var/lib/enma/dataprotection-keys
RUN install -d -o app -g app -m 0700 /var/lib/enma/dataprotection-keys
WORKDIR /app
COPY --from=build /out/api ./
COPY --from=web /src/Enma.Web/dist ./wwwroot
USER app
EXPOSE 8080
ENTRYPOINT ["dotnet", "Enma.Api.dll"]
