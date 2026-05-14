# syntax=docker/dockerfile:1.7
#
# Spring Voyage — Dapr Agent container.
#
# Platform-managed agentic loop using Dapr Agents + A2A protocol.
# Designed to run alongside a Dapr sidecar that provides the Conversation
# building block (Ollama, OpenAI, Anthropic, etc.).

# Pinned to an exact patch and aligned to Python 3.13 to match
# eng/build/Dockerfile.agent-base — every Python-bearing platform image
# now tracks the same interpreter line. The SDK declares
# `requires-python = ">=3.12"` (agents/spring-voyage-agent-sdk/pyproject.toml),
# so 3.13 satisfies the floor; CI still validates on 3.12 to keep the
# lower-bound honest (.github/workflows/ci.yml, python-test job).
FROM docker.io/library/python:3.13.13-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    AGENT_PORT=8999

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
        tini \
 && rm -rf /var/lib/apt/lists/*

# Non-root user.
ARG AGENT_UID=1000
ARG AGENT_GID=1000
RUN groupadd --gid "${AGENT_GID}" agent \
 && useradd --uid "${AGENT_UID}" --gid "${AGENT_GID}" --create-home --shell /bin/bash agent

WORKDIR /app

# Copy the Spring Voyage Agent SDK source so it can be installed from /sdk.
# The SDK is a sibling directory (../spring-voyage-agent-sdk) relative to this
# Dockerfile; the Docker build context must be set to the agents/ parent.
# If building with: docker build -f spring-voyage-agent/Dockerfile agents/
# the COPY paths below work as-is.
COPY spring-voyage-agent-sdk/ /sdk/

COPY dapr-agent/requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt \
 && pip install --no-cache-dir /sdk

COPY dapr-agent/ .

RUN chown -R agent:agent /app
USER agent

EXPOSE ${AGENT_PORT}

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["python", "agent.py"]
