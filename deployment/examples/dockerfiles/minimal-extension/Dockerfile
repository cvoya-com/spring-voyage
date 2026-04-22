# syntax=docker/dockerfile:1.7
#
# Minimal extension of a Spring Voyage agent base image.
#
# This is the smallest possible custom agent image: take one of the
# tool-bearing agent images shipped by `deployment/build-agent-images.sh`
# (PR 3b of #1087, #1096) and tag it under your own registry / namespace.
# Useful when you want a stable, pinned tag to reference from a unit or
# agent manifest even though no extra tooling is needed.
#
# Pick the right base for your tool. The default below is the Claude Code
# image; swap in `localhost/spring-voyage-agent-dapr:latest` for the Dapr
# Agent path-3 runtime, or `ghcr.io/cvoya-com/agent-base:1.0.0` if you only
# want the bridge sidecar and will install your own CLI on top.
#
# Build it with:
#   podman build -t localhost/my-agent:latest .
#   # or: docker build -t localhost/my-agent:latest .
#
# Reference it from a unit manifest:
#   unit:
#     name: my-team
#     execution:
#       image: localhost/my-agent:latest

FROM localhost/spring-voyage-agent-claude-code:latest
