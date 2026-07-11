# Pinned to an exact patch and aligned to Python 3.13 to match the rest of
# the platform's Python images (eng/build/Dockerfile.agent-base,
# agents/spring-voyage-agent/Dockerfile, eng/build/Dockerfile.agent.dapr).
# Only depends on aiohttp, which supports 3.13 natively.
FROM docker.io/library/python:3.13.13-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
 && useradd --uid 10001 --create-home --shell /usr/sbin/nologin sidecar \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY sidecar.py .

RUN chown -R sidecar:sidecar /app

ENV AGENT_PORT=8999

EXPOSE ${AGENT_PORT}

USER sidecar

ENTRYPOINT ["python3", "sidecar.py"]
