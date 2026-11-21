-- Copies of the api's configuration, fed by Kafka `config.changes` (contracts/schemas/processor.md, "Copies").
-- `body` is the source of truth; the other columns are copied from it for inspection.

CREATE TABLE IF NOT EXISTS mappings (
    id         text PRIMARY KEY,
    status     text,
    priority   int,
    body       jsonb NOT NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS rules (
    id         text PRIMARY KEY,
    team_id    text,
    enabled    boolean,
    body       jsonb NOT NULL,
    updated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS service_deps (
    service    text,
    depends_on text,
    PRIMARY KEY (service, depends_on)
);
