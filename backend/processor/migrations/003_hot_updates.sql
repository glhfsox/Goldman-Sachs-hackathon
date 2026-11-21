-- Count flushes change count, last_seen, last_received_at and severity; with none of them in an index the update
-- can be HOT (no index writes, less bloat). The reopen lookup sorts a key's few rows itself (contracts/schemas/processor.md).
DROP INDEX IF EXISTS alerts_key_recent;
CREATE INDEX IF NOT EXISTS alerts_key ON alerts (alert_key);
-- HOT also needs room in the row's page; at 100 (the default) only 10-17% of flush updates were HOT. New pages only.
ALTER TABLE alerts SET (fillfactor = 70);
