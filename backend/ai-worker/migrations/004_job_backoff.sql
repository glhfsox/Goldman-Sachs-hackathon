-- A retried job waits until `run_after` (exponential backoff per job); see contracts/schemas/ai-worker.md.
ALTER TABLE jobs ADD COLUMN IF NOT EXISTS run_after timestamptz;
