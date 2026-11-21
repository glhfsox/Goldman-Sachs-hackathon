-- Unmapped events kept for replay once a mapping for their schema becomes active (contracts/schemas/processor.md).
CREATE TABLE IF NOT EXISTS parked_events (
    seq           bigserial PRIMARY KEY,      -- replay order
    event_id      text NOT NULL UNIQUE,       -- a redelivered record parks once
    fingerprint   text NOT NULL,
    event         jsonb NOT NULL,             -- the raw event of events.md §2, payload whole
    matched_rules text[] NOT NULL,            -- rules counted at arrival; the replay skips them
    created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS parked_events_fingerprint ON parked_events (fingerprint);
