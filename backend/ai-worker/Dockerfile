# Build from backend/ai-worker with the processor's package as the extra context `processor`
# (docker-compose.yml sets it): the mapping tool reuses processor.core and processor.models.
FROM python:3.12-slim
RUN pip install --no-cache-dir uv==0.12.9
WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-cache
COPY --from=processor __init__.py core.py models.py processor/
COPY migrations migrations
COPY scripts scripts
COPY ai_worker ai_worker
ENV PATH="/app/.venv/bin:$PATH" PYTHONUNBUFFERED=1
CMD ["python", "-m", "ai_worker"]
