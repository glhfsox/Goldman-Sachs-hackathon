import glob
import os
import shutil
import socket
import subprocess
from pathlib import Path

import psycopg
import pytest
from langgraph.checkpoint.postgres import PostgresSaver

MIGRATIONS = Path(__file__).parent.parent / "migrations"


def _pg_bindir():
    for path in [None, *sorted(glob.glob("/usr/lib/postgresql/*/bin"), reverse=True)]:
        if (initdb := shutil.which("initdb", path=path)) and shutil.which("pg_ctl", path=os.path.dirname(initdb)):
            return os.path.dirname(initdb)


@pytest.fixture(scope="session")
def pg_url(tmp_path_factory):
    if url := os.environ.get("TEST_DATABASE_URL"):
        yield url
        return
    bindir = _pg_bindir()
    if bindir is None:
        pytest.skip("no PostgreSQL binaries (initdb, pg_ctl) on PATH or in /usr/lib/postgresql/*/bin")
    root = tmp_path_factory.mktemp("pg")
    data = root / "data"
    subprocess.run([f"{bindir}/initdb", "-D", data, "-U", "postgres", "-A", "trust", "-E", "UTF8", "--no-sync"],
                   check=True, capture_output=True)
    with socket.socket() as s:  # a free loopback port, never the default 5432
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    with open(data / "postgresql.conf", "a") as f:
        f.write(f"\nport = {port}\nlisten_addresses = '127.0.0.1'\nunix_socket_directories = ''\nfsync = off\n")
    subprocess.run([f"{bindir}/pg_ctl", "-D", data, "-l", root / "log", "-w", "start"], check=True, capture_output=True)
    try:
        yield f"postgresql://postgres@127.0.0.1:{port}/postgres"
    finally:
        subprocess.run([f"{bindir}/pg_ctl", "-D", data, "-m", "immediate", "stop"], capture_output=True)


@pytest.fixture(scope="session")
def db(pg_url):
    with psycopg.connect(pg_url, autocommit=True) as c:
        for f in sorted(MIGRATIONS.glob("*.sql")):
            c.execute(f.read_text())
        PostgresSaver(c).setup()  # jobs.finish deletes a finished job's checkpoints
    return pg_url


@pytest.fixture(autouse=True)
def metrics_file(tmp_path, monkeypatch):
    path = tmp_path / "agent-metrics.jsonl"
    monkeypatch.setenv("AGENT_METRICS_FILE", str(path))
    return path


@pytest.fixture
def conns(db):
    """(autocommit connection for enqueue and asserts, transactional connection for claims) on empty tables."""
    with psycopg.connect(db, autocommit=True) as pg, psycopg.connect(db) as jobs_pg:
        pg.execute("TRUNCATE jobs, alert_history, run_history, checkpoints, checkpoint_blobs, checkpoint_writes")
        yield pg, jobs_pg
