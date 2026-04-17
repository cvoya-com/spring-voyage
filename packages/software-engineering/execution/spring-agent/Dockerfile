# spring-agent — ephemeral/persistent Claude Code runtime image (#390).
#
# The base image bundles the .NET SDK (for the Spring sidecar adapter the
# launcher expects on port 8999), git, and Node.js so the Claude Code CLI can
# be installed from npm. The CLI is pinned so the image is reproducible —
# override at build time with --build-arg CLAUDE_CODE_VERSION=<x.y.z> to roll
# forward without editing this Dockerfile.
#
# Anthropic distributes @anthropic-ai/claude-code on the public npm registry;
# no vendored tarball is required. The npm package exposes a single binary
# named `claude` (not `claude-code`) — that is what this image's entrypoint
# invokes and what the Spring launcher / smoke tests reference.
#
# The image does NOT bake Anthropic credentials. The caller (spring platform
# or operator) supplies an API key / auth context via environment variables at
# launch time — ClaudeCodeLauncher.cs stamps the SPRING_* env vars the sidecar
# adapter consumes. No interactive login is embedded.
FROM mcr.microsoft.com/dotnet/sdk:10.0

# Pin explicitly so `docker build` is reproducible across hosts. Override with
# --build-arg CLAUDE_CODE_VERSION=<x.y.z> to roll forward without editing this
# file; CI tags the resulting image with both the version and the image tag so
# operators can audit which CLI release shipped.
ARG CLAUDE_CODE_VERSION=2.1.98
ARG NODE_MAJOR=20

# git — required by Claude Code's workspace operations.
# curl + ca-certificates + gnupg — required to add the NodeSource apt repo.
# Node.js is installed from the NodeSource distribution (multi-arch: amd64 +
# arm64) so the image works on both Apple-silicon developer laptops and
# x86_64 CI runners.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        curl \
        ca-certificates \
        gnupg \
    && mkdir -p /etc/apt/keyrings \
    && curl -fsSL https://deb.nodesource.com/gpgkey/nodesource-repo.gpg.key \
        | gpg --dearmor -o /etc/apt/keyrings/nodesource.gpg \
    && echo "deb [signed-by=/etc/apt/keyrings/nodesource.gpg] https://deb.nodesource.com/node_${NODE_MAJOR}.x nodistro main" \
        > /etc/apt/sources.list.d/nodesource.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends nodejs \
    && rm -rf /var/lib/apt/lists/*

# Install the Claude Code CLI from the public npm registry. The bin entry is
# `claude` (see package.json on npm). We probe both `--version` (prints the
# installed release) so any install breakage fails the build loudly instead
# of producing a silently broken image.
RUN npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && command -v claude >/dev/null \
    && claude --version

WORKDIR /workspace
ENTRYPOINT ["claude"]
