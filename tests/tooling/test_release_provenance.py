from copy import deepcopy
import unittest

from release_provenance import POLICIES, bind_producer, resolve_candidate, verify_source


class ReleaseProvenanceTests(unittest.TestCase):
    def setUp(self):
        self.kind = "host"
        self.policy = POLICIES[self.kind]
        self.prefix = "repos/" + self.policy["repository"] + "/actions"
        self.commit = "a" * 40
        self.replies = {}
        for identity in (11, 12):
            self.replies[self.prefix + "/runs/" + str(identity)] = {
                "id": identity, "repository": {"full_name": self.policy["repository"], "id": 31},
                "path": self.policy["workflow"], "event": "workflow_dispatch", "head_branch": "main",
                "workflow_id": 21, "head_sha": self.commit, "run_attempt": 2}
        self.artifact = {"id": 41, "name": "host-candidate-11-2", "expired": False, "size_in_bytes": 80,
            "digest": "sha256:" + "b" * 64, "created_at": "2026-11-03T10:01:00Z",
            "workflow_run": {"id": 11, "repository_id": 31, "head_repository_id": 31,
                             "head_sha": self.commit, "head_branch": "main"}}
        self.artifacts_path = self.prefix + "/runs/11/artifacts?per_page=100&page=1"
        self.jobs_path = self.prefix + "/runs/11/attempts/2/jobs?per_page=100&page=1"
        self.replies[self.artifacts_path] = {"total_count": 1, "artifacts": [self.artifact]}
        self.jobs = [{"id": index + 51, "name": name, "status": "completed", "conclusion": "success",
            "run_id": 11, "head_sha": self.commit, "started_at": "2026-11-03T10:00:00Z",
            "completed_at": "2026-11-03T10:02:00Z"} for index, name in enumerate(self.policy["jobs"])]
        self.replies[self.jobs_path] = {"total_count": len(self.jobs), "jobs": self.jobs}

    def resolve(self):
        return resolve_candidate(lambda path: self.replies[path], self.kind, 11, 12)

    def test_accepts_candidate(self):
        result = self.resolve()
        self.assertEqual((11, 2, 41), (result["runId"], result["runAttempt"], result["artifactId"]))
        self.assertTrue(result["completedJobsVerified"])
        self.assertEqual({"repository": self.policy["repository"], "workflow": self.policy["workflow"],
            "workflowCommit": self.commit, "runId": 12, "runAttempt": 2}, result["writer"])
        self.replies[self.prefix + "/runs/11"]["conclusion"] = "failure"
        self.assertEqual(result, self.resolve())

    def test_rejects_attempts(self):
        baseline = deepcopy(self.replies)
        for field, value in (("head_branch", "develop"), ("event", "push"), ("workflow_id", 99),
                             ("path", ".github/workflows/untrusted.yml"), ("run_attempt", 3)):
            self.replies = deepcopy(baseline)
            self.replies[self.prefix + "/runs/11"][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.resolve()
        with self.assertRaises(ValueError):
            resolve_candidate(lambda path: baseline[path], "host", 11, 11)

    def test_rejects_artifacts(self):
        baseline = deepcopy(self.replies)
        for field, value in (("expired", True), ("size_in_bytes", 0), ("digest", None),
                             ("created_at", "2026-11-03T11:00:00Z"), ("name", "host-candidate-11-1")):
            self.replies = deepcopy(baseline)
            self.replies[self.artifacts_path]["artifacts"][0][field] = value
            with self.subTest(field=field), self.assertRaises(ValueError):
                self.resolve()
        self.replies = deepcopy(baseline)
        self.replies[self.artifacts_path]["artifacts"].append(deepcopy(self.artifact))
        self.replies[self.artifacts_path]["total_count"] = 2
        with self.assertRaises(ValueError):
            self.resolve()
        self.replies = deepcopy(baseline)
        self.replies[self.artifacts_path]["total_count"] = 2
        with self.assertRaises(ValueError):
            self.resolve()

    def test_rejects_jobs(self):
        baseline = deepcopy(self.replies)
        for field, value in (("conclusion", "failure"), ("conclusion", "skipped"), ("status", "in_progress"),
                             ("run_id", 12), ("head_sha", "c" * 40), ("name", "other")):
            self.replies = deepcopy(baseline)
            self.replies[self.jobs_path]["jobs"][0][field] = value
            with self.subTest(field=field, value=value), self.assertRaises(ValueError):
                self.resolve()

    def test_binds_source(self):
        identity = {"sourceMode": "git-commit", "sourceCommit": self.commit,
                    "sourceTree": "b" * 40, "sourceSnapshotHash": "c" * 64}
        prefix = "repos/" + self.policy["repository"]
        replies = {prefix + "/commits/" + self.commit: {"sha": self.commit, "commit": {"tree": {"sha": "b" * 40}}},
            prefix + "/commits/main": {"sha": "d" * 40}, prefix + f"/compare/{self.commit}..." + "d" * 40:
            {"status": "ahead", "base_commit": {"sha": self.commit}, "merge_base_commit": {"sha": self.commit}}}
        def fetch(path):
            return replies[path]

        result = verify_source(fetch, self.policy["repository"], identity)
        self.assertEqual("d" * 40, result["protectedCommit"])
        for field in ("sourceMode", "sourceTree", "sourceSnapshotHash"):
            altered = {**identity, field: "invalid"}
            with self.subTest(field=field), self.assertRaises(ValueError):
                verify_source(fetch, self.policy["repository"], altered)
        replies[prefix + f"/compare/{self.commit}..." + "d" * 40]["status"] = "behind"
        with self.assertRaises(ValueError):
            verify_source(fetch, self.policy["repository"], identity)

    def test_binds_producer(self):
        provenance = self.resolve()
        producer = {name: provenance[name] for name in ("repository", "workflow", "workflowCommit", "runId", "runAttempt")}
        candidate = {"format": "nexus.release.candidate", "version": 1, "profile": "production", "producer": producer}
        self.assertEqual(candidate, bind_producer(candidate, provenance))
        for field, value in (("profile", "test"), ("version", True), ("producer", {**producer, "runAttempt": 1})):
            with self.subTest(field=field), self.assertRaises(ValueError):
                bind_producer({**candidate, field: value}, provenance)
