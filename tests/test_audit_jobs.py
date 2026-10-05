import importlib.util
import os
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
    @patch.dict(os.environ, {"GITHUB_API_URL": "https://api.github.com"})
    def test_synthetic_records_require_service_identity_and_physical_failures_remain(self):
        real = {**job(1), "name": "Final Budget", "runner_id": 8, "labels": ["windows"], "steps": [{}], "conclusion": "failure"}
        synthetic = {**job(2), "runner_id": None, "labels": [], "steps": [],
                     "check_run_url": "https://api.github.com/repos/owner/repo/check-runs/2"}
        check = {"id": 2, "head_sha": "a" * 40, "app": {"id": 17}, "name": "Final Budget", "external_id": "nxp-budget-v2:7:12:2:22:1:" + "a" * 40,
                 "output": {"title": "Complete CI job budget", "summary": "Run 12, attempt 1: old result"}}
        with patch.object(audit_jobs, "api", return_value=check):
            physical, checks = audit_jobs.physical_jobs("owner/repo", {"id": 12, "run_attempt": 2, "head_sha": "a" * 40}, [real, synthetic], "Final Budget", 17)
        self.assertEqual(physical, [real])
        self.assertEqual(checks, [check])
        self.assertEqual(audit_jobs.audit(physical, 12, 2)[0]["status"], "FAIL")
        for field, value in [("id", 3), ("head_sha", "b" * 40), ("app", {"id": 18}), ("name", "unknown"), ("external_id", "")]:
            with patch.object(audit_jobs, "api", return_value={**check, field: value}):
                with self.assertRaises(ValueError):
                    audit_jobs.physical_jobs("owner/repo", {"id": 12, "run_attempt": 2, "head_sha": "a" * 40}, [real, synthetic], "Final Budget", 17)

    def test_skipped_matrix_selector_is_not_an_allocated_runner(self):
        run={"id":12,"run_attempt":2,"head_sha":"a"*40}
        for prefix in ["Host", "Plugins"]:
            skipped={**job(2), "name":"matrix.name || '"+prefix+" / 验证批次（未选中）'",
                     "conclusion":"skipped", "labels":["windows-2025"], "steps":[], "runner_id":None}
            physical,_=audit_jobs.physical_jobs("owner/repo",run,[skipped],"Final Budget",17)
            self.assertEqual(physical[0]["name"],prefix+" / 验证批次（未选中）")
            self.assertTrue(physical[0]["runnerlessSkipped"])
            for change in [{"runner_id":8}, {"steps":[{}]}, {"conclusion":"failure"}, {"name":"unknown"}]:
                physical,_=audit_jobs.physical_jobs("owner/repo",run,[{**skipped,**change}],"Final Budget",17)
                self.assertNotIn("runnerlessSkipped",physical[0])

    def test_empty_duplicate_and_naive_times_fail(self):
        for jobs in [[], [job(1), job(1)], [{**job(1), "started_at": "2026-09-29T00:00:00"}]]:
            with self.assertRaises(ValueError):
                audit_jobs.audit(jobs, 12, 2)

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
