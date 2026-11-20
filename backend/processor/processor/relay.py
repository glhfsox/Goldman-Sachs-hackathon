"""Outbox relay: publishes `outbox` rows to Kafka in id order, deletes them once Kafka confirmed (storage.md)."""

import json
import logging

from confluent_kafka import KafkaError, KafkaException
from psycopg.types.json import Jsonb

from .store import UI_CHANNEL

log = logging.getLogger(__name__)

LOCK_ID = 0x6F7574626F78  # "outbox": one replica drains at a time, so per-key order holds across replicas
FLUSH_TIMEOUT_S = 10
MESSAGE_TIMEOUT_MS = (FLUSH_TIMEOUT_S - 1) * 1000  # the producer's message.timeout.ms: below the flush timeout
_REJECTED = (KafkaError.MSG_SIZE_TOO_LARGE, KafkaError.INVALID_MSG_SIZE, KafkaError.INVALID_MSG)  # retrying never helps


class Relay:
    """`redis` must be created with decode_responses=True. The producer must keep order on retries
    (enable.idempotence) and give up on a message before the flush does (message.timeout.ms = MESSAGE_TIMEOUT_MS),
    so every message of a drain gets its final callback inside that drain and is never produced twice."""

    def __init__(self, conn, producer, redis):
        self.pg, self.kafka, self.r = conn, producer, redis

    def drain(self, limit: int = 500) -> int:
        """Publish pending rows; return how many were published and deleted. Raises only on a Postgres failure."""
        with self.pg.transaction():
            # the xact lock is held until the deletes commit, so a second replica never sees rows already sent
            if not self.pg.execute("SELECT pg_try_advisory_xact_lock(%s)", (LOCK_ID,)).fetchone()[0]:
                return 0
            rows = self.pg.execute("SELECT id, topic, key, payload FROM outbox ORDER BY id LIMIT %s", (limit,)).fetchall()
            if not rows:
                return 0
            errors = {}  # row id -> KafkaError or None; a row missing here was never confirmed
            rejected = {}  # row id -> the error of a row Kafka refused for good
            for row_id, topic, key, payload in rows:
                try:
                    self.kafka.produce(topic, key=key,
                                       value=json.dumps(payload, separators=(",", ":"), ensure_ascii=False).encode(),
                                       on_delivery=lambda err, msg, row_id=row_id: errors.__setitem__(row_id, err))
                except KafkaException as e:
                    if not (e.args and isinstance(e.args[0], KafkaError) and e.args[0].code() in _REJECTED):
                        log.exception("kafka produce failed; the rest of the batch stays in the outbox")
                        break
                    rejected[row_id] = str(e.args[0])
                except Exception:
                    log.exception("kafka produce failed; the rest of the batch stays in the outbox")
                    break
            try:
                if queued := self.kafka.flush(FLUSH_TIMEOUT_S):
                    log.warning("%d message(s) still queued after flush; their rows count as failed", queued)
            except Exception:
                log.exception("kafka flush failed")
            done, dead = [], []
            for row in rows:
                if row[0] in rejected:
                    dead.append(row)
                    continue
                if (err := errors.get(row[0], "not confirmed")) is not None:
                    log.warning("outbox row %s not delivered (%s); it and later rows are retried", row[0], err)
                    break
                done.append(row)
            for row_id, topic, key, payload in dead:
                log.error("outbox row %s to %s rejected by kafka (%s); moved to outbox_dead", row_id, topic, rejected[row_id])
                self.pg.execute("INSERT INTO outbox_dead (id, topic, key, payload, error) VALUES (%s, %s, %s, %s, %s)",
                                (row_id, topic, key, Jsonb(payload), rejected[row_id]))
            if done or dead:
                self.pg.execute("DELETE FROM outbox WHERE id = ANY(%s)", ([row[0] for row in done + dead],))
        ui = [row[3] for row in done if row[1] == "alerts.changes"]
        if ui:
            try:
                pipe = self.r.pipeline(transaction=False)
                for p in ui:
                    pipe.publish(UI_CHANNEL, json.dumps({"type": "alert", "change": p["change"], "alert": p["alert"]}))
                pipe.execute()
            except Exception:
                log.exception("redis publish to %s failed; the UI refetches", UI_CHANNEL)
        return len(done)
