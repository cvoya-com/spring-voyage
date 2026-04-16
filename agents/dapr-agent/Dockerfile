# syntax=docker/dockerfile:1.7
#
# Spring Voyage — Dapr Agent container.
#
# Platform-managed agentic loop using Dapr Agents + A2A protocol.
# Designed to run alongside a Dapr sidecar that provides the Conversation
# building block (Ollama, OpenAI, Anthropic, etc.).

FROM docker.io/library/python:3.12-slim

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

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY . .

RUN chown -R agent:agent /app
USER agent

EXPOSE ${AGENT_PORT}

ENTRYPOINT ["/usr/bin/tini", "--"]
CMD ["python", "agent.py"]
