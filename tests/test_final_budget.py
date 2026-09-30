import importlib.util
import os
import copy
import sys
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
    @patch.dict(os.environ, {"GITHUB_API_URL": "https://api.github.com", "GITHUB_REPOSITORY": REPOSITORY,
                             "GITHUB_RUN_ID": "22", "GITHUB_SHA": "b" * 40})
    def test_begin_finalize_rerun_late_and_duplicate_events(self):
        source = run()
        source["run_number"] = 5
        source["status"] = "queued"
        source["pull_requests"][0]["number"] = 7
        checks, writes = [], []
        current_event = ["workflow_run"]
        controller = {"id": 22, "run_attempt": 1, "head_sha": "b" * 40, "path": ".github/workflows/final-budget.yml", "check_suite_id": 3,
                      "repository": {"full_name": REPOSITORY}, "conclusion": "success","event":"workflow_run","head_branch":"main"}
        jobs = [{**job(1, "Required"), "labels": ["windows"]}]
        def api(route):
            if "/pulls/7" in route: return source["pull_requests"][0]
            if "/check-suites/3" in route: return {"app": {"id": 17}}
            if "/check-runs?" in route: return {"total_count": len(checks), "check_runs": copy.deepcopy(checks)}
            if "/workflows/ci.yml/runs?" in route: return {"total_count": 1, "workflow_runs": [copy.deepcopy(source)]}
            if "/runs/22" in route:
                if "/jobs?" in route: return {"total_count": 1, "jobs": [{**job(8, "begin"), "run_id": 22, "run_attempt": 1}]}
                return {**copy.deepcopy(controller), "event": controller["event"] if "/attempts/" in route else current_event[0]}
            if "/runs/12" in route:
                if "/jobs?" in route:
                    rows = [{**row, "run_attempt": 1} for row in jobs] if "/attempts/1/" in route else copy.deepcopy(jobs)
                    return {"total_count": len(rows), "jobs": rows}
                return copy.deepcopy(source)
            raise AssertionError(route)
        def write(route, body, method):
            if body["status"] == "in_progress":
                self.assertNotIn("conclusion", body)
            writes.append((method, copy.deepcopy(body)))
            result = {**(checks[0] if checks else {}), **body, "id": 77, "head_sha": SHA, "app": {"id": 17}, "html_url": "fixture"}
            if body["status"] == "in_progress": result["conclusion"] = None
            checks[:] = [result]
            return copy.deepcopy(result)
        def event(phase, attempt=2):
            with patch.object(sys, "argv", ["final-budget.py", "--run-id", "12", "--attempt", str(attempt), "--required-name", "Required", "--check-name", "Budget", "--phase", phase]):
                final_budget.main()
        with patch.object(final_budget.audit_jobs, "api", side_effect=api), patch.object(final_budget, "write_api", side_effect=write):
            event("begin")
            self.assertEqual(checks[0]["status"], "in_progress")
            event("begin")
            self.assertEqual(len(writes), 1)
            source["status"] = "completed"
            event("finalize")
            self.assertEqual(checks[0]["conclusion"], "success")
            current_event[0] = "workflow_dispatch"
            event("finalize")
            count = len(writes)
            event("begin")
            self.assertEqual(len(writes), count)
            controller["event"] = "workflow_dispatch"
            self.assertEqual([item[0] for item in writes], ["POST", "PATCH", "PATCH"])
            source["run_attempt"] = 3
            jobs[0]["run_attempt"] = 3
            event("begin", 3)
            self.assertEqual(checks[0]["status"], "in_progress")
            self.assertIsNone(checks[0]["conclusion"])
            count = len(writes)
            with self.assertRaises(ValueError): event("finalize", 2)
            self.assertEqual(len(writes), count)
            jobs[0]["conclusion"] = "failure"
            with self.assertRaises(SystemExit) as failure: event("finalize", 3)
            self.assertEqual(failure.exception.code, 1)
            self.assertEqual(checks[0]["conclusion"], "failure")

    def test_registration_rejects_legacy_and_malformed_identity(self):
        for value in ["", "nxp-budget-v2:7:12:0:22:1:" + "a" * 40, "nxp-budget-v2:7:12:2:22:1:bad"]:
            with self.assertRaises(ValueError): final_budget.registration({"external_id": value})

    @patch.dict(os.environ,{"GITHUB_API_URL":"https://api.github.com"})
    def test_new_batch_graph_rejects_sixth_hidden_or_missing_scope(self):
        jobs=[job(1,"Host / Required"),job(2,"Host / 范围判定"),job(3,"Host / Control")]
        self.assertTrue(final_budget.evaluate(run(),jobs,REPOSITORY,12,2,"Host / Required")[2])
        for extra in [[job(9,"hidden")],[job(10,"Host / batch-06")], [job(index+20,f"Host / batch-{index:02d}") for index in range(1,7)]]:
            with self.assertRaises(ValueError): final_budget.evaluate(run(),jobs+extra,REPOSITORY,12,2,"Host / Required")
        with self.assertRaises(ValueError): final_budget.evaluate(run(),[jobs[0],jobs[2]],REPOSITORY,12,2,"Host / Required")

    @patch.dict(os.environ,{"GITHUB_API_URL":"https://api.github.com"})
    def test_old_head_or_older_run_is_superseded(self):
        source=run();source["run_number"]=5;pull=source["pull_requests"][0];pull["number"]=7
        with patch.object(final_budget.audit_jobs,"api",return_value={**pull,"head":{"sha":"b"*40}}):
            self.assertFalse(final_budget.is_current(REPOSITORY,source,pull))
        newer={**source,"id":13,"run_number":6}
        with patch.object(final_budget.audit_jobs,"api",side_effect=[pull,source]),patch.object(final_budget.audit_jobs,"paged",return_value=[source,newer]):
            self.assertFalse(final_budget.is_current(REPOSITORY,source,pull))

    def test_partial_rerun_does_not_borrow_old_jobs(self):
        old = [{**job(1, "Required"), "run_attempt": 1, "labels": ["windows"]},
               {**job(2, "business"), "run_attempt": 1, "labels": ["windows"]}]
        with patch.object(final_budget.audit_jobs, "completed_jobs", return_value=old):
            with self.assertRaisesRegex(ValueError, "INCOMPLETE_ATTEMPT"):
                final_budget.require_complete_attempt(REPOSITORY, run(), [job(1, "Required")], "Budget", 17)

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
