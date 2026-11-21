# Sources simulator

Plays the third-party systems of [contracts/data/sources.md](../../contracts/data/sources.md): it generates events and
sends them to ingest over Kafka (`source.*` on `kafka-sources`) and HTTP (`POST /ingest/{source_id}`).
The story it tells: [data/README.md](../README.md).

## Run

```bash
cd data/simulator
bash check.sh                               # creates .venv and installs once, then lint + tests
source .venv/bin/activate

sim list                                    # schemas and scenarios
sim sample trade_event -n 2                 # example payloads (nothing is sent)
sim run demo --dry-run | head               # what would be sent, as JSON lines
sim run demo                                # the real thing: needs the broker and ingest
```

`sim run` needs the sources broker (`cd ../kafka-sources && docker compose up -d`) for Kafka and ingest on
port 8001 for HTTP. Without ingest, add `--only kafka`. Addresses: `SOURCE_KAFKA_BROKERS` (default `localhost:9094`),
`INGEST_URL` (default `http://localhost:8001`), or `--brokers` / `--ingest-url`.

| Scenario | What |
|---|---|
| `demo` | The Kestrel Markets morning, about 3.5 minutes, same events every run (seeded) |
| `steady` | About 50 msg/s of mixed normal traffic until Ctrl-C |
| `burst` | The same settlement failure 500 times a minute for a minute (dedup) |
| `load` | About 1000 msg/s, 90% Kafka; `--rate 5000` to scale, `--duration 30` to stop |
| `edge` | Every edge case of contract §4.5: unknown source, invalid JSON, raw text, new schema, non-ASCII, repeats |

Useful flags: `--duration S`, `--rate N`, `--rate-scale X`, `--only kafka|http`, `--seed N`, `--concurrency N`.

## How it is built

| Part | File | Add one by |
|---|---|---|
| Sources | `sources.yaml`: source id → Kafka topic + key path, or HTTP | one entry (and the contract's registry) |
| Schemas | `schemas/*.yaml`: one payload type each, fields built by `$` rules | one YAML file |
| Scenarios | `scenarios/*.yaml`: phases of streams with rates, counts and overrides | one YAML file |
| Sinks | `sim/sinks.py`: `KafkaSink`, `HttpSink`, `StdoutSink` | one class |

Schema rules (`sim/generate.py`): `$choice` (+ `$weights`), `$faker`, `$int`, `$float`, `$lognormal`, `$now`,
`$pattern` (`WF-{date}-{seq:04d}`, `#` digit, `?` letter), `$map` (pick by an earlier field), `$ref`, `$const`;
modifiers `$p` (probability present) and `$if` (present only when an earlier field matches).

Senders follow the contract: HTTP retries `503`, timeouts and connection errors with backoff (6 attempts) and
never retries `404`; Kafka uses an idempotent producer with `acks=all`.

Measured on a laptop against the local broker: 4300 msg/s over Kafka with no failures and no lost or duplicated messages.
