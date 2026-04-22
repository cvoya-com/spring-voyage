# syntax=docker/dockerfile:1.7
#
# Custom agent image with extra CLI tooling.
#
# Extends a Spring Voyage tool-bearing agent image (the tags produced by
# `deployment/build-agent-images.sh`, added in PR 3b of #1087 / #1096)
# with a couple of common CLI tools the agent process can shell out to.
# Use this as a template when an agent workflow needs a tool that isn't
# in the base image (e.g. a specific MCP server binary, a protobuf
# compiler, or a domain-specific linter).
#
# The example below extends the Claude Code path-1 image. Swap the FROM
# for `localhost/spring-voyage-agent-dapr:latest` when extending the
# Dapr Agent (path 3) runtime, or for `ghcr.io/cvoya-com/agent-base:1.0.0`
# when you want a bare bridge image with your own CLI on top.
#
# Build it with:
#   podman build -t localhost/my-agent-with-tools:latest .
#   # or: docker build -t localhost/my-agent-with-tools:latest .

FROM localhost/spring-voyage-agent-claude-code:latest

# `jq` and `ripgrep` are already in the agent-base image (see
# `deployment/Dockerfile.agent-base`). Add your own layer of tooling here.
# Examples below; un-comment or replace with what your agent needs.

USER root

# Example: install hyperfine (CLI benchmarking tool).
#
# RUN apt-get update \
#  && apt-get install -y --no-install-recommends hyperfine \
#  && rm -rf /var/lib/apt/lists/*

# Example: install a protobuf compiler.
#
# RUN apt-get update \
#  && apt-get install -y --no-install-recommends protobuf-compiler \
#  && rm -rf /var/lib/apt/lists/*

# Example: pin a Node-based MCP server as a global npm install.
#
# RUN npm install -g @your-org/your-mcp-server@1.2.3

USER agent
