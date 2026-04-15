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
COPY .config/ .config/
COPY src/ src/

RUN dotnet restore src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj \
 && dotnet restore src/Cvoya.Spring.Host.Worker/Cvoya.Spring.Host.Worker.csproj \
 && dotnet restore src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj

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
# Stage 2: build the Next.js dashboard (npm workspace at repo root)
# ------------------------------------------------------------------
# The Web project is a member of the repo-root npm workspace alongside
# sibling connector packages under src/Cvoya.Spring.Connector.*/web.
# Copy workspace manifests first for cacheable `npm ci`, then sources.
FROM node:22-bookworm-slim AS web-build
WORKDIR /build

COPY package.json package-lock.json ./
COPY src/Cvoya.Spring.Web/package.json ./src/Cvoya.Spring.Web/
COPY src/Cvoya.Spring.Connector.GitHub/web/package.json ./src/Cvoya.Spring.Connector.GitHub/web/

RUN npm ci

# Web sources + the committed OpenAPI doc that the dashboard's `prebuild`
# hook consumes to regenerate typed API clients.
COPY src/Cvoya.Spring.Web/ ./src/Cvoya.Spring.Web/
COPY src/Cvoya.Spring.Connector.GitHub/web/ ./src/Cvoya.Spring.Connector.GitHub/web/
COPY src/Cvoya.Spring.Host.Api/openapi.json ./src/Cvoya.Spring.Host.Api/openapi.json

# next.config.ts sets `output: 'standalone'` with outputFileTracingRoot
# pinned to the workspace root — the standalone tree preserves the
# `src/Cvoya.Spring.Web/` path, so server.js lives nested in the output.
RUN npm run build --workspace=spring-voyage-dashboard

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

# Copy Next.js standalone output. With outputFileTracingRoot pinned to the
# monorepo root, the standalone tree mirrors the workspace layout — the
# server entrypoint is at /app/web/src/Cvoya.Spring.Web/server.js.
COPY --from=web-build /build/src/Cvoya.Spring.Web/.next/standalone /app/web/
COPY --from=web-build /build/src/Cvoya.Spring.Web/.next/static     /app/web/src/Cvoya.Spring.Web/.next/static

# Copy Dapr component YAML and Dapr Configuration for both the local and
# production profiles. Sidecars select a profile via `--resources-path`
# (e.g. `/dapr/components/production`) and `--config` (e.g.
# `/dapr/config/production.yaml`). Files are read-only at runtime; override
# by mounting another directory at /dapr.
COPY dapr /dapr

# Ship the unit-template + skill-bundle catalog so the wizard's Template
# step (#260) and skill-bundle resolution (#167) work out of the box.
# FileSystemPackageCatalogService and FileSystemSkillBundleResolver both
# read from this tree; SPRING_PACKAGES_ROOT in spring.env must match.
COPY packages /packages

EXPOSE 8080
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

# Drop to a non-root user. mcr.microsoft.com/dotnet/aspnet pre-creates
# the 'app' user (uid 1654) specifically for this purpose.
USER app

# Default command runs the Worker. Override in `podman run` for api/web.
CMD ["dotnet", "/app/Cvoya.Spring.Host.Worker.dll"]
