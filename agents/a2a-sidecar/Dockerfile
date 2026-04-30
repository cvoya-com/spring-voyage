FROM python:3.12-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        ca-certificates \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY sidecar.py .

ENV AGENT_PORT=8999

EXPOSE ${AGENT_PORT}

ENTRYPOINT ["python3", "sidecar.py"]
