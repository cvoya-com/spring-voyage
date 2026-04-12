# syntax=docker/dockerfile:1.7
#
# Spring Voyage — platform image.
#
# Contains the published .NET 10 API and Worker hosts, the Web dashboard
# (Next.js standalone output), and the Dapr CLI. One image serves the
# spring-worker, spring-api, and spring-web containers — the command the
# container is started with selects the process.

# ------------------------------------------------------------------
# Stage 1: build the .NET hosts
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS dotnet-build
WORKDIR /src

# Copy solution manifests first for better layer caching.
COPY SpringVoyage.slnx global.json Directory.Build.props Directory.Packages.props ./
COPY NuGet.config* ./
COPY src/ src/

RUN dotnet restore SpringVoyage.slnx

RUN dotnet publish src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj \
        --configuration Release --no-restore \
        --output /publish/api \
 && dotnet publish src/Cvoya.Spring.Host.Worker/Cvoya.Spring.Host.Worker.csproj \
        --configuration Release --no-restore \
        --output /publish/worker \
 && dotnet publish src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj \
        --configuration Release --no-restore \
        --output /publish/cli

# ------------------------------------------------------------------
# Stage 2: build the Next.js dashboard
# ------------------------------------------------------------------
FROM node:22-bookworm-slim AS web-build
WORKDIR /web

COPY src/Cvoya.Spring.Web/package.json src/Cvoya.Spring.Web/package-lock.json ./
RUN npm ci

COPY src/Cvoya.Spring.Web/ ./
# next.config.ts must set `output: 'standalone'` for this to succeed.
# If it doesn't, the build still emits .next/; the runtime falls back to `next start`.
RUN npm run build

# ------------------------------------------------------------------
# Stage 3: runtime image
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install Dapr CLI and Node.js runtime (for the web container).
# Dapr CLI is included so operators can invoke `dapr` against the local sidecar
# (status, metadata, etc.) from within the container.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        curl ca-certificates gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && curl -fsSL https://raw.githubusercontent.com/dapr/cli/master/install/install.sh | /bin/bash -s 1.14.1 \
 && apt-get purge -y --auto-remove gnupg \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

# Copy published .NET output. All binaries live side-by-side in /app so the
# default entrypoint resolves whichever DLL the command specifies.
COPY --from=dotnet-build /publish/api/    /app/
COPY --from=dotnet-build /publish/worker/ /app/
COPY --from=dotnet-build /publish/cli/    /app/cli/

# Copy Next.js standalone output (if produced) plus fallback.
COPY --from=web-build /web/.next/standalone /app/web/
COPY --from=web-build /web/.next/static     /app/web/.next/static
COPY --from=web-build /web/public           /app/web/public

# Copy Dapr component YAML and Dapr Configuration for both the local and
# production profiles. Sidecars select a profile via `--resources-path`
# (e.g. `/dapr/components/production`) and `--config` (e.g.
# `/dapr/config/production.yaml`). Files are read-only at runtime; override
# by mounting another directory at /dapr.
COPY dapr /dapr

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Drop to a non-root user. mcr.microsoft.com/dotnet/aspnet pre-creates
# the 'app' user (uid 1654) specifically for this purpose.
USER app

# Default command runs the Worker. Override in `podman run` for api/web.
CMD ["dotnet", "/app/Cvoya.Spring.Host.Worker.dll"]
