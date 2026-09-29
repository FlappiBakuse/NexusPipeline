import contextlib
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock


SOURCE = Path(__file__).with_name("gate-required.py")
SPEC = importlib.util.spec_from_file_location("gate_required", SOURCE)
GATE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(GATE)
ROOT = SOURCE.parents[1]


class RequiredTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="host-required-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name) / "source"
        self.reports = Path(self.temporary.name) / "reports"
        (self.root / "tests").mkdir(parents=True)
        self.reports.mkdir()
        for name in ("gates.json", "policy.json"):
            (self.root / "tests" / name).write_bytes((ROOT / "tests" / name).read_bytes())
        registry = json.loads((self.root / "tests/gates.json").read_bytes())
        declared = {gate["id"] for gate in registry["gates"]}
        self.plan = {"repository": "Host", "runId": "12", "attempt": "2",
                     "testedSha": "a" * 40, "dirty": False,
                     "policyDigest": GATE.canonical_digest(self.root / "tests/gates.json"),
                     "digestFormat": "utf8-lf-v1",
                     "selected": [{"id": "host.scope"}, {"id": "host.required"}],
                     "notApplicable": [{"id": item} for item in sorted(declared - {"host.scope", "host.required"})]}
        self.job = {"id": 1, "name": next(gate["name"] for gate in registry["gates"] if gate["id"] == "host.scope"),
                    "run_id": 12, "run_attempt": 2, "status": "completed", "conclusion": "success",
                    "started_at": "2026-09-29T00:00:00Z", "completed_at": "2026-09-29T00:02:30Z"}

    def check(self, gate_result="skipped"):
        (self.reports / "scope.json").write_text(json.dumps(self.plan), encoding="utf-8")
        environ = {"GITHUB_RUN_ID": "12", "GITHUB_RUN_ATTEMPT": "2", "GITHUB_SHA": "a" * 40,
                   "GITHUB_REPOSITORY": "FlappiBakuse/NexusPipeline",
                   "CI_NEEDS": json.dumps({"scope": {"result": "success"}, "gates": {"result": gate_result}})}
        with mock.patch.dict(os.environ, environ), mock.patch.object(sys, "argv", ["gate-required.py", str(self.root), str(self.reports)]), \
                mock.patch.object(GATE, "all_jobs", return_value=[self.job]), contextlib.redirect_stdout(io.StringIO()):
            GATE.main()

    def test_document_only_empty_matrix_is_explicitly_disposed(self):
        self.check()

    def test_full_job_post_time_above_150_seconds_fails(self):
        self.job["completed_at"] = "2026-09-29T00:02:31Z"
        with self.assertRaisesRegex(ValueError, "150 seconds"):
            self.check()

    def test_selected_gate_cannot_be_skipped_or_lack_report(self):
        self.plan["selected"].append({"id": "host.docs"})
        self.plan["notApplicable"] = [item for item in self.plan["notApplicable"] if item["id"] != "host.docs"]
        with self.assertRaisesRegex(ValueError, "Selected matrix"):
            self.check()
        with self.assertRaisesRegex(ValueError, "Missing or unexpected gate report"):
            self.check("success")


if __name__ == "__main__":
    unittest.main()
