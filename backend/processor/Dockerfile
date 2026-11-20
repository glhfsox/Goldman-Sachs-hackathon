FROM python:3.12-slim
RUN pip install --no-cache-dir uv==0.12.9
WORKDIR /app
COPY pyproject.toml uv.lock ./
RUN uv sync --frozen --no-dev --no-cache
COPY migrations migrations
COPY processor processor
ENV PATH="/app/.venv/bin:$PATH" PYTHONUNBUFFERED=1
CMD ["python", "-m", "processor"]
