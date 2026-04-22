# syntax=docker/dockerfile:1.7
#
# Custom agent image with extra CLI tooling.
#
# Extends the Spring Voyage `localhost/spring-voyage-agent:latest` base
# (the tag produced by `deployment/deploy.sh build`) with a couple of
# common CLI tools the agent process can shell out to. Use this as a
# template when an agent workflow needs a tool that isn't in the base
# image (e.g. a specific MCP server binary, a protobuf compiler, or a
# domain-specific linter).
#
# Build it with:
#   podman build -t localhost/my-agent-with-tools:latest .
#   # or: docker build -t localhost/my-agent-with-tools:latest .

FROM localhost/spring-voyage-agent:latest

# `jq` and `ripgrep` are already in the base image (see
# `deployment/Dockerfile.agent`). Add your own layer of tooling here.
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
