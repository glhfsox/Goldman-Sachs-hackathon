from ai_worker.main import alert_job, config_job, sample_job

RUNBOOK = {"id": "rb-1", "team_id": "settlements", "title": "Settlement timeout", "format": "markdown",
           "source": "# steps", "canonical": None, "status": "draft", "version": 2}


def test_schema_sample_becomes_mapping_job():
    rec = {"fingerprint": "9f2c", "source_id": "trades-kafka", "seen_at": "2026-11-20T10:15:02.123Z",
           "sample": {"meta": {"schema": "trade.rejected.v1"}}}
    assert sample_job(rec) == ("schema:9f2c", "mapping",
                               {"fingerprint": "9f2c", "source_id": "trades-kafka", "sample": rec["sample"]})
    assert sample_job({"source_id": "x", "sample": {}}) is None


def test_runbook_draft_becomes_job_and_others_do_not():
    rec = {"kind": "runbook", "id": "rb-1", "updated_at": "2026-11-20T09:30:00.000Z", "deleted": False, "body": RUNBOOK}
    assert config_job(rec, {}) == ("runbook:rb-1:2", "runbook", RUNBOOK)
    assert config_job({**rec, "body": {**RUNBOOK, "canonical": {"steps": []}}}, {}) is None
    assert config_job({**rec, "body": {**RUNBOOK, "status": "active"}}, {}) is None


def test_mapping_records_keep_the_copy_current():
    mappings = {}
    body = {"id": "m1", "status": "active"}
    assert config_job({"kind": "mapping", "id": "m1", "deleted": False, "body": body}, mappings) is None
    assert mappings == {"m1": body}
    config_job({"kind": "mapping", "id": "m1", "deleted": True, "body": None}, mappings)
    assert mappings == {}
    assert config_job({"kind": "rule", "id": "r1", "deleted": False, "body": {"id": "r1"}}, mappings) is None


def test_only_resolved_alert_changes_become_proposal_jobs():
    rec = {"change_id": "c1", "change": "resolved", "at": "2026-11-20T10:21:00.000Z", "actor": "marek",
           "alert": {"id": "A1", "rule_id": "r"}}
    assert alert_job(rec) == ("proposal:A1", "proposal", {"alert_id": "A1"})
    assert alert_job({**rec, "actor": "auto"}) == ("proposal:A1", "proposal", {"alert_id": "A1"})
    for change in ("opened", "acked", "reopened", "severity_raised"):
        assert alert_job({**rec, "change": change}) is None
    assert alert_job({**rec, "alert": None}) is None
