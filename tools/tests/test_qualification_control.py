from __future__ import annotations

import unittest

from tools.qualification_control import QualificationError, collect_gate_results, read_pull_request, resolve_candidate


H = "a" * 40
B = "b" * 40


class FakeApi:
    def __init__(self, responses: dict[tuple[str, str], object]) -> None:
        self.responses = responses

    def __call__(self, method: str, path: str, _token: str | None, _payload: object) -> object:
        response = self.responses.get((method, path))
        if response is None:
            raise AssertionError(f"unexpected request: {method} {path}")
        return response


def pull_payload(*, head_repo: str = "FlappiBakuse/NexusPipeline") -> dict:
    return {
        "state": "open",
        "base": {"ref": "main", "repo": {"full_name": "FlappiBakuse/NexusPipeline"}},
        "head": {"sha": H, "repo": {"full_name": head_repo}},
    }


class QualificationControlTests(unittest.TestCase):
    def test_read_pull_rejects_fork(self) -> None:
        api = FakeApi({("GET", "/repos/FlappiBakuse/NexusPipeline/pulls/7"): pull_payload(head_repo="someone/fork")})
        with self.assertRaisesRegex(QualificationError, "同一官方仓库"):
            read_pull_request("FlappiBakuse/NexusPipeline", 7, H, "secret", request_fn=api)

    def test_resolve_candidate_requires_c_equal_b_and_ancestor(self) -> None:
        api = FakeApi({("GET", "/repos/FlappiBakuse/NexusPipeline/git/ref/heads/main"): {"object": {"sha": B}}})
        with self.assertRaisesRegex(QualificationError, "C 必须等于"):
            resolve_candidate("FlappiBakuse/NexusPipeline", H, "c" * 40, "secret", request_fn=api)

        class Result:
            returncode = 1

        with self.assertRaisesRegex(QualificationError, "祖先"):
            resolve_candidate("FlappiBakuse/NexusPipeline", H, B, "secret", request_fn=api, git_runner=lambda _args: Result())

    def test_collect_gate_results_rejects_skipped_gate(self) -> None:
        jobs = [
            {"name": "H1", "run_id": 12, "status": "completed", "conclusion": "success", "run_attempt": 2, "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "success"}]},
            {"name": "H2", "run_id": 12, "status": "completed", "conclusion": "skipped", "run_attempt": 2, "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "success"}]},
            {"name": "H3", "run_id": 12, "status": "completed", "conclusion": "success", "run_attempt": 2, "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "success"}]},
            {"name": "H4", "run_id": 12, "status": "completed", "conclusion": "success", "run_attempt": 2, "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "success"}]},
            {"name": "H5", "run_id": 12, "status": "completed", "conclusion": "success", "run_attempt": 2, "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "success"}]},
        ]
        api = FakeApi({("GET", "/repos/FlappiBakuse/NexusPipeline/actions/runs/12/attempts/2/jobs?per_page=100&page=1"): {"jobs": jobs}})
        with self.assertRaisesRegex(QualificationError, "H2"):
            collect_gate_results("FlappiBakuse/NexusPipeline", 12, "secret", run_attempt=2, request_fn=api)


if __name__ == "__main__":
    unittest.main()
