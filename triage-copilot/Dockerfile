FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app ./app
RUN useradd --system --no-create-home appuser
USER appuser

EXPOSE 8082
# Healthy only when the readiness marker exists (queue bound, Redis answering) AND /health returns 200.
# slim has no curl, so the check is Python standard library only. 30s start: waits for broker and Redis.
HEALTHCHECK --interval=10s --timeout=3s --start-period=30s --retries=3 \
  CMD ["python", "-m", "app.healthcheck"]

CMD ["uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8082"]
