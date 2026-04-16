FROM python:3.12-slim

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY sidecar.py .

ENV AGENT_PORT=8999

EXPOSE ${AGENT_PORT}

ENTRYPOINT ["python3", "sidecar.py"]
