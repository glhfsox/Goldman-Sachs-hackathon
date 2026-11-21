from datetime import UTC, datetime

import pytest

from sim.generate import Context

FIXED_NOW = datetime(2026, 11, 20, 10, 15, 1, 204000, tzinfo=UTC)


@pytest.fixture
def ctx():
    return Context(seed=1, now=lambda: FIXED_NOW)


class FakeClock:
    """Time that only moves when the code under test sleeps: plays minutes of scenario in milliseconds."""

    def __init__(self):
        self.t = 0.0

    def __call__(self):
        return self.t

    async def sleep(self, s):
        self.t += max(s, 0.001)


class Collect:
    """A sink that keeps every event."""

    def __init__(self, stats):
        self.stats, self.events = stats, []

    async def send(self, event):
        self.events.append(event)
        self.stats.ok(event.source_id)

    async def close(self):
        pass
