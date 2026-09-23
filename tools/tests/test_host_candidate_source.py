from __future__ import annotations

import copy
import unittest

from tools.host_candidate_source import CandidateSourceError, resolve_candidate


REPO = "FlappiBakuse/NexusPipeline"
PREFIX = f"repos/{REPO}/actions"
SHA = "a" * 40


class CandidateSourceTests(unittest.TestCase):
    def fixture(self):
        run = {"id": 12, "repository": {"full_name": REPO}, "workflow_id": 88,
               "path": ".github/workflows/release.yml", "event": "push", "head_branch": "main",
               "head_sha": SHA, "conclusion": "failure"}
        current = {"id": 99, "repository": {"full_name": REPO}, "workflow_id": 88}
        artifact = {"id": 42, "name": "host-candidate-12-2", "expired": False,
                    "size_in_bytes": 500, "workflow_run": {"id": 12}, "digest": "sha256:" + "b" * 64}
        paths = {
            f"{PREFIX}/runs/12": run,
            f"{PREFIX}/runs/99": current,
            f"{PREFIX}/runs/12/artifacts?per_page=100&page=1": {"artifacts": [artifact]},
            f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100":
                {"jobs": [{"name": "Build and validate Host candidate", "status": "completed", "conclusion": "success"}]},
        }
        return paths

    def resolve(self, paths, artifact_id=None):
        return resolve_candidate(lambda path: paths[path], candidate_run_id=12,
                                 current_run_id=99, artifact_id=artifact_id)

    def test_original_job_success_survives_writer_workflow_failure(self):
        result = self.resolve(self.fixture())
        self.assertEqual((result["sourceSha"], result["runAttempt"], result["artifactId"]), (SHA, 2, 42))

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
        paths[f"{PREFIX}/runs/12/attempts/2/jobs?per_page=100"]["jobs"][0]["conclusion"] = "cancelled"
        with self.assertRaisesRegex(CandidateSourceError, "未真实成功"):
            self.resolve(paths)

    def test_diagnostics_artifact_cannot_be_candidate(self):
        paths = self.fixture()
        paths[f"{PREFIX}/runs/12/artifacts?per_page=100&page=1"]["artifacts"][0]["name"] = "host-diagnostics-12-2"
        with self.assertRaisesRegex(CandidateSourceError, "不存在"):
            self.resolve(paths)


if __name__ == "__main__":
    unittest.main()
