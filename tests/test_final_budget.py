import importlib.util
import os
from pathlib import Path
import unittest
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("final_budget", Path(__file__).with_name("final-budget.py"))
final_budget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(final_budget)


SHA = "a" * 40
REPOSITORY = "owner/repo"


def run():
    return {"id": 12, "run_attempt": 2, "status": "completed", "event": "pull_request",
            "path": ".github/workflows/ci.yml", "repository": {"full_name": REPOSITORY},
            "head_sha": SHA, "conclusion": "success",
            "pull_requests": [{"base": {"ref": "main", "repo": {
                "url": "https://api.github.com/repos/" + REPOSITORY}},
                "head": {"sha": SHA}}]}


def job(index, name, end="2026-09-29T00:02:30Z"):
    return {"id": index, "name": name, "run_id": 12, "run_attempt": 2,
            "status": "completed", "conclusion": "success",
            "started_at": "2026-09-29T00:00:00Z", "completed_at": end}


class FinalBudgetTests(unittest.TestCase):
    @patch.dict(os.environ, {"GITHUB_API_URL": "https://api.github.com"})
    def test_complete_job_boundary_and_required(self):
        jobs = [job(1, "Required"), job(2, "gate")]
        self.assertTrue(final_budget.evaluate(run(), jobs, REPOSITORY, 12, 2, "Required")[2])
        jobs[0]["completed_at"] = "2026-09-29T00:02:30.001Z"
        self.assertFalse(final_budget.evaluate(run(), jobs, REPOSITORY, 12, 2, "Required")[2])
        jobs.pop(0)
        with self.assertRaises(ValueError):
            final_budget.evaluate(run(), jobs, REPOSITORY, 12, 2, "Required")

    @patch.dict(os.environ, {"GITHUB_API_URL": "https://api.github.com"})
    def test_rejects_foreign_and_incomplete_run(self):
        source = run()
        source["path"] = ".github/workflows/other.yml"
        with self.assertRaises(ValueError):
            final_budget.evaluate(source, [job(1, "Required")], REPOSITORY, 12, 2, "Required")
        source = run()
        source["status"] = "in_progress"
        with self.assertRaises(ValueError):
            final_budget.evaluate(source, [job(1, "Required")], REPOSITORY, 12, 2, "Required")
        source = run()
        source["pull_requests"][0]["head"]["sha"] = "b" * 40
        with self.assertRaises(ValueError):
            final_budget.evaluate(source, [job(1, "Required")], REPOSITORY, 12, 2, "Required")

    @patch.dict(os.environ, {"GITHUB_API_URL": "https://api.github.com"})
    def test_merged_pr_relation_is_recovered_from_commit(self):
        source = run()
        linked = source["pull_requests"]
        source["pull_requests"] = []
        with patch.object(final_budget.audit_jobs, "api", return_value=linked) as api:
            self.assertTrue(final_budget.evaluate(source, [job(1, "Required")],
                                                  REPOSITORY, 12, 2, "Required")[2])
            api.assert_called_once_with(f"/repos/{REPOSITORY}/commits/{SHA}/pulls?per_page=100")
        with patch.object(final_budget.audit_jobs, "api", return_value=linked * 2):
            with self.assertRaises(ValueError):
                final_budget.evaluate(source, [job(1, "Required")], REPOSITORY, 12, 2, "Required")


if __name__ == "__main__":
    unittest.main()
