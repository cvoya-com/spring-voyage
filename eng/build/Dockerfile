# syntax=docker/dockerfile:1-labs
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
# eng/runtime-catalog/runtime-catalog.yaml is embedded as a resource by
# src/Cvoya.Spring.RuntimeCatalog/Cvoya.Spring.RuntimeCatalog.csproj
# (ADR-0038); the build fails with CS1566 if this directory is missing.
COPY eng/runtime-catalog/ eng/runtime-catalog/

RUN dotnet restore src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj \
 && dotnet restore src/Cvoya.Spring.Host.Worker/Cvoya.Spring.Host.Worker.csproj \
 && dotnet restore src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj \
 && dotnet build-server shutdown

RUN dotnet publish src/Cvoya.Spring.Host.Api/Cvoya.Spring.Host.Api.csproj \
        --configuration Release --no-restore \
        --output /publish/api \
 && dotnet publish src/Cvoya.Spring.Host.Worker/Cvoya.Spring.Host.Worker.csproj \
        --configuration Release --no-restore \
        --output /publish/worker \
 && dotnet publish src/Cvoya.Spring.Cli/Cvoya.Spring.Cli.csproj \
        --configuration Release --no-restore \
        --output /publish/cli \
 && dotnet build-server shutdown

# ------------------------------------------------------------------
# Stage 2: build the Next.js dashboard (npm workspace at repo root)
# ------------------------------------------------------------------
# The Web project is a member of the repo-root npm workspace alongside
# sibling connector packages under src/Cvoya.Spring.Connector.*/web.
# Copy workspace manifests first for cacheable `npm ci`, then sources.
# The connector-web COPYs use `COPY --parents` with the same wildcard
# the npm workspace itself globs on, so a new connector that ships a
# web/ submodule is picked up automatically — no per-connector edit to
# this Dockerfile, no silent drift between release-time builds and CI
# (#2783).
# Pinned to an exact patch so rebuilds are reproducible. Bump in lockstep
# with eng/build/Dockerfile.agent-base when rolling Node forward.
FROM docker.io/library/node:22.22.2-bookworm-slim AS web-build
WORKDIR /build

# Manifest layer for `npm ci` — `--parents` preserves each matched
# file's directory structure so workspace members land at their real
# paths under ./src/. Keeps the npm-ci RUN cacheable: it only re-fires
# when a workspace manifest or the lockfile actually changes.
COPY package.json package-lock.json ./
COPY --parents src/Cvoya.Spring.Web/package.json ./
COPY --parents src/Cvoya.Spring.Connector.*/web/package.json ./
# preinstall hook needs this — keep the path narrow so unrelated eng/ci/
# changes don't bust the npm-ci layer cache.
COPY eng/ci/enforce-npm.cjs ./eng/ci/enforce-npm.cjs

RUN npm ci

# Web sources + every connector's web/ tree (picked up by the same
# wildcard) + the committed OpenAPI doc that the dashboard's `prebuild`
# hook consumes to regenerate typed API clients.
COPY --parents src/Cvoya.Spring.Web/ ./
COPY --parents src/Cvoya.Spring.Connector.*/web/ ./
COPY src/Cvoya.Spring.Host.Api/openapi.json ./src/Cvoya.Spring.Host.Api/openapi.json

# next.config.ts sets `output: 'standalone'` with outputFileTracingRoot
# pinned to the workspace root — the standalone tree preserves the
# `src/Cvoya.Spring.Web/` path, so server.js lives nested in the output.
RUN npm run build --workspace=spring-voyage-dashboard

# ------------------------------------------------------------------
# Stage 3: runtime image
# ------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# Install Dapr CLI. Node.js runtime binary is copied from web-build (see below).
# Dapr CLI is included so operators can invoke `dapr` against the local sidecar
# (status, metadata, etc.) from within the container.
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        curl ca-certificates \
 && curl -fsSL https://raw.githubusercontent.com/dapr/cli/master/install/install.sh | /bin/bash -s 1.14.1 \
 && rm -rf /var/lib/apt/lists/*

COPY --from=web-build /usr/local/bin/node /usr/local/bin/node

WORKDIR /app

# Copy published .NET output. All binaries live side-by-side in /app so the
# default entrypoint resolves whichever DLL the command specifies.
COPY --from=dotnet-build /publish/api/    /app/
COPY --from=dotnet-build /publish/worker/ /app/
COPY --from=dotnet-build /publish/cli/    /app/cli/

# Copy Next.js standalone output. With outputFileTracingRoot pinned to the
# monorepo root, the standalone tree mirrors the workspace layout — the
# server entrypoint is at /app/web/src/Cvoya.Spring.Web/server.js.
#
# `next build` deliberately omits `public/` from the standalone tree
# (see https://nextjs.org/docs/app/api-reference/config/next-config-js/output#automatically-copying-traced-files),
# so without an explicit COPY the brand-mark PNGs under `public/brand/`
# return 404 in containerized deploys and the sidebar header renders a
# broken image. Mirror the workspace layout so `server.js` resolves
# `public/` next to itself, the same way `next start` would.
COPY --from=web-build /build/src/Cvoya.Spring.Web/.next/standalone /app/web/
COPY --from=web-build /build/src/Cvoya.Spring.Web/.next/static     /app/web/src/Cvoya.Spring.Web/.next/static
COPY --from=web-build /build/src/Cvoya.Spring.Web/public           /app/web/src/Cvoya.Spring.Web/public

# Copy Dapr component YAML and Dapr Configuration for both the local and
# production profiles. Sidecars select a profile via `--resources-path`
# (e.g. `/dapr/components/production`) and `--config` (e.g.
# `/dapr/config/production.yaml`). Files are read-only at runtime; override
# by mounting another directory at /dapr.
COPY eng/dapr /dapr

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
