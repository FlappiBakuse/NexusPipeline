from __future__ import annotations

import copy
import io
import unittest
import urllib.error
from unittest import mock

from tools.host_candidate_source import CandidateSourceError, REQUIRED_JOBS, github_fetch, resolve_candidate


REPO = "FlappiBakuse/NexusPipeline"
PREFIX = f"repos/{REPO}/actions"
SHA = "e6ff62b0f7ae32d83c0452229744f3ee55823657"


class CandidateSourceTests(unittest.TestCase):
    def fixture(self):
        run = {"id": 12, "repository": {"full_name": REPO}, "workflow_id": 88,
               "path": ".github/workflows/release.yml", "event": "push", "head_branch": "main",
               "head_sha": SHA, "conclusion": "failure", "run_attempt": 2}
        current = {"id": 99, "repository": {"full_name": REPO}, "workflow_id": 88}
        artifact = {"id": 42, "name": "host-candidate-12-2", "expired": False,
                    "size_in_bytes": 500, "workflow_run": {"id": 12}, "digest": "sha256:" + "b" * 64}
        paths = {
            f"{PREFIX}/runs/12": run,
            f"{PREFIX}/runs/99": current,
            f"{PREFIX}/runs/12/artifacts?per_page=100&page=1": {"artifacts": [artifact]},
            f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100&page=1":
                {"jobs": [{"name": name, "status": "completed", "conclusion": "success",
                           "started_at": "2026-10-04T00:00:00Z", "completed_at": "2026-10-04T00:02:30Z"}
                          for name in REQUIRED_JOBS]},
        }
        return paths

    def resolve(self, paths, artifact_id=None):
        return resolve_candidate(lambda path: paths[path], candidate_run_id=12,
                                 current_run_id=99, artifact_id=artifact_id)

    def test_original_job_success_survives_writer_workflow_failure(self):
        result = self.resolve(self.fixture())
        self.assertEqual((result["sourceSha"], result["runAttempt"], result["artifactId"]), (SHA, 2, 42))

    def test_main_controlled_manual_candidate_is_accepted(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12"]["event"] = "workflow_dispatch"
        result = self.resolve(paths)
        self.assertEqual((result["workflowSha"], result["runAttempt"]), (SHA, 2))

    def test_foreign_workflow_or_repository_is_rejected(self):
        for key, value in (("workflow_id", 89), ("repository", {"full_name": "someone/else"})):
            paths = self.fixture()
            paths[f"{PREFIX}/runs/12"][key] = value
            with self.subTest(key=key), self.assertRaises(CandidateSourceError):
                self.resolve(paths)

    def test_ambiguous_artifacts_require_exact_id(self):
        paths = self.fixture()
        other = copy.deepcopy(paths[f"{PREFIX}/runs/12/artifacts?per_page=100&page=1"]["artifacts"][0])
        other.update(id=43, name="host-candidate-12-3")
        paths[f"{PREFIX}/runs/12/artifacts?per_page=100&page=1"]["artifacts"].append(other)
        with self.assertRaisesRegex(CandidateSourceError, "不唯一"):
            self.resolve(paths)
        self.assertEqual(self.resolve(paths, artifact_id=42)["artifactId"], 42)

    def test_original_candidate_job_must_have_succeeded(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100&page=1"]["jobs"][0]["conclusion"] = "cancelled"
        with self.assertRaisesRegex(CandidateSourceError, "未真实成功"):
            self.resolve(paths)

    def test_retired_producer_and_old_attempt_are_rejected(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100&page=1"] = {"jobs": [
            {"name": "Build and validate Host candidate", "status": "completed", "conclusion": "success"}]}
        with self.assertRaisesRegex(CandidateSourceError, "已退役"):
            self.resolve(paths)
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12"]["run_attempt"] = 3
        with self.assertRaisesRegex(CandidateSourceError, "过期 attempt"):
            self.resolve(paths)

    def test_staged_producer_requires_every_completed_job_with_full_budget(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12"]["head_sha"] = "c" * 40
        jobs = [{"name": name, "status": "completed", "conclusion": "success",
                 "started_at": "2026-09-29T00:00:00Z", "completed_at": "2026-09-29T00:02:30Z"}
                for name in REQUIRED_JOBS]
        paths[f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100&page=1"] = {"jobs": jobs}
        self.assertTrue(self.resolve(paths)["budgetQualified"])
        jobs[-1]["completed_at"] = "2026-09-29T00:02:31Z"
        with self.assertRaisesRegex(CandidateSourceError, "超过 150 秒"):
            self.resolve(paths)
        jobs[-1]["completed_at"] = "2026-09-29T00:02:30Z"
        jobs[0]["conclusion"] = "skipped"
        with self.assertRaisesRegex(CandidateSourceError, "未真实成功"):
            self.resolve(paths)

    def test_diagnostics_artifact_cannot_be_candidate(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12/artifacts?per_page=100&page=1"]["artifacts"][0]["name"] = "host-diagnostics-12-2"
        with self.assertRaisesRegex(CandidateSourceError, "不存在"):
            self.resolve(paths)

    @mock.patch("tools.host_candidate_source.time.sleep")
    @mock.patch("tools.host_candidate_source.urllib.request.urlopen")
    def test_actions_reads_retry_only_three_times_for_transient_failures(self, urlopen, sleep):
        response = mock.MagicMock()
        response.__enter__.return_value = io.BytesIO(b'{"ok": true}')
        urlopen.side_effect = [
            urllib.error.HTTPError("https://api.github.test", 429, "limited", {"Retry-After": "9"}, io.BytesIO()),
            urllib.error.HTTPError("https://api.github.test", 503, "temporary", {}, io.BytesIO()),
            response,
        ]
        self.assertEqual(github_fetch("token", "path"), {"ok": True})
        self.assertEqual(urlopen.call_count, 3)
        self.assertEqual([call.args[0] for call in sleep.call_args_list], [5, 1])

        urlopen.reset_mock()
        sleep.reset_mock()
        urlopen.side_effect = [TimeoutError("timeout")] * 3
        with self.assertRaisesRegex(CandidateSourceError, "TimeoutError"):
            github_fetch("token", "path")
        self.assertEqual(urlopen.call_count, 3)


if __name__ == "__main__":
    unittest.main()
