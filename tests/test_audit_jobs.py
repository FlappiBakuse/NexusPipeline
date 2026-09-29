import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("audit_jobs", Path(__file__).with_name("audit-jobs.py"))
audit_jobs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit_jobs)


def job(index, end="2026-09-29T00:02:30Z"):
    return {"id": index, "name": f"gate-{index}", "run_id": 12, "run_attempt": 2,
            "status": "completed", "conclusion": "success",
            "started_at": "2026-09-29T00:00:00Z", "completed_at": end}


class AuditJobsTests(unittest.TestCase):
    def test_qualification_boundary_includes_full_job(self):
        self.assertEqual(audit_jobs.audit([job(1)], 12, 2)[0]["status"], "PASS")
        self.assertEqual(audit_jobs.audit([job(1, "2026-09-29T00:02:30.001Z")], 12, 2)[0]["status"], "FAIL")
        self.assertEqual(audit_jobs.audit([job(1, "2026-09-29T00:02:59Z")], 12, 2)[0]["status"], "FAIL")

    def test_incomplete_and_wrong_attempt_fail(self):
        unfinished = job(1)
        unfinished["completed_at"] = None
        with self.assertRaises(ValueError):
            audit_jobs.audit([unfinished], 12, 2)
        with self.assertRaises(ValueError):
            audit_jobs.audit([job(1)], 12, 3)

    def test_all_pages_are_required(self):
        pages = [{"total_count": 101, "jobs": [job(index) for index in range(100)]},
                 {"total_count": 101, "jobs": [job(100)]}]
        with patch.object(audit_jobs, "api", side_effect=pages) as mocked:
            self.assertEqual(len(audit_jobs.completed_jobs("owner/repo", 12, 2)), 101)
            self.assertEqual(mocked.call_count, 2)
        pages[1]["jobs"] = []
        with patch.object(audit_jobs, "api", side_effect=pages):
            with self.assertRaises(ValueError):
                audit_jobs.completed_jobs("owner/repo", 12, 2)


if __name__ == "__main__":
    unittest.main()
