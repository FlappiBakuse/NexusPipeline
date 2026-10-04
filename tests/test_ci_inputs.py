import base64
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
import ci_inputs as inputs


def block(pair):
    return "```nexus-ci-pair\n" + json.dumps(pair) + "\n```"


class InputPairTests(unittest.TestCase):
    def setUp(self):
        self.responses = {}
        for index, repo in enumerate(inputs.REPOSITORIES):
            base, head, tested, tree, controller = [str(index * 5 + offset) * 40 for offset in range(1, 6)]
            if index:
                controller = "a" * 40
            number = index + 1
            self.responses[f"/repos/{repo}/pulls/{number}"] = {
                "number": number, "state": "open", "body": None, "merge_commit_sha": tested,
                "base": {"sha": base, "ref": "main", "repo": {"full_name": repo}},
                "head": {"sha": head, "repo": {"full_name": repo}}}
            self.responses[f"/repos/{repo}/git/ref/pull/{number}/merge"] = {"object": {"sha": tested}}
            self.responses[f"/repos/{repo}/git/ref/heads/main"] = {"object": {"sha": controller}}
            self.responses[f"/repos/{repo}/git/commits/{tested}"] = {
                "sha": tested, "tree": {"sha": tree}, "parents": [{"sha": base}, {"sha": head}]}
            self.responses[f"/repos/{repo}/git/trees/{controller}?recursive=1"] = {
                "truncated": False, "tree": [{"type": "blob", "path": "tests/ci_inputs.py", "sha": "b" * 40}]}
            self.responses[f"/repos/{repo}/compare/{controller}...main"] = {
                "status": "identical", "merge_base_commit": {"sha": controller}}
            for name in ["tests/policy.json", "plugins.lock.json" if index == 0 else "tests/inputs.lock.json"]:
                self.responses[f"/repos/{repo}/contents/{name}?ref={tested}"] = {
                    "type": "file", "encoding": "base64", "content": base64.b64encode(b'{"schemaVersion":1}\n').decode()}
        self.pair = inputs.create_pair([1, 2], self.read)
        for source in self.pair["sources"]:
            self.responses[f"/repos/{source['repository']}/pulls/{source['prNumber']}"]["body"] = block(self.pair)

    def read(self, route):
        return copy.deepcopy(self.responses[route])

    def test_canonical_fixed_vector_and_duplicate_unknown_fields(self):
        self.assertEqual(inputs.canonical({"z": [1, "中文"], "a": {"b": 2}}),
                         '{"a":{"b":2},"z":[1,"中文"]}'.encode())
        self.assertEqual(inputs.digest({"b": 2, "a": 1}),
                         hashlib.sha256(b'{"a":1,"b":2}').hexdigest())
        with self.assertRaises(ValueError):
            inputs.parse('{"a":1,"a":2}')
        for mutation in [lambda p: p.update(extra=1), lambda p: p.update(schemaVersion=True),
                         lambda p: p["sources"][0].update(prNumber=True),
                         lambda p: p["sources"].reverse(), lambda p: p.update(pairDigest="0" * 64)]:
            pair = copy.deepcopy(self.pair)
            mutation(pair)
            with self.assertRaises(ValueError):
                inputs.source_pair(block(pair))

    def test_absent_pair_is_default_but_malformed_or_duplicate_never_falls_back(self):
        self.assertIsNone(inputs.source_pair("Ordinary PR body"))
        for body in ["```nexus-ci-pair\n{}", block(self.pair) * 2, "nexus-ci-pair", block({})]:
            with self.assertRaises(ValueError):
                inputs.source_pair(body)

    def test_resolves_real_merge_inputs_and_distinguishes_head(self):
        own = self.pair["sources"][0]
        self.assertEqual(inputs.resolve(own["repository"], own["prNumber"], head=own["headSha"],
                         base=own["baseSha"], tested=own["testedSha"], read=self.read), self.pair)
        with self.assertRaises(ValueError):
            inputs.resolve(own["repository"], own["prNumber"], tested=own["headSha"], read=self.read)

    def test_both_repository_changes_invalidate_pair(self):
        for source in self.pair["sources"]:
            prefix = f"/repos/{source['repository']}"
            cases = [
                (f"{prefix}/pulls/{source['prNumber']}", lambda p: p["head"].update(sha="f" * 40)),
                (f"{prefix}/pulls/{source['prNumber']}", lambda p: p["base"].update(sha="f" * 40)),
                (f"{prefix}/pulls/{source['prNumber']}", lambda p: p.update(body="pair removed")),
                (f"{prefix}/git/ref/pull/{source['prNumber']}/merge", lambda p: p["object"].update(sha="f" * 40)),
                (f"{prefix}/git/commits/{source['testedSha']}", lambda p: p["tree"].update(sha="f" * 40)),
                (f"{prefix}/git/commits/{source['testedSha']}", lambda p: p["parents"].reverse()),
                (f"{prefix}/contents/tests/policy.json?ref={source['testedSha']}", lambda p: p.update(content="e30=")),
                (f"{prefix}/compare/{source['controllerSha']}...main", lambda p: p.update(status="diverged")),
                (f"{prefix}/git/trees/{source['controllerSha']}?recursive=1", lambda p: p.update(truncated=True)),
            ]
            for route, mutate in cases:
                previous = copy.deepcopy(self.responses[route])
                mutate(self.responses[route])
                with self.subTest(route=route), self.assertRaises(ValueError):
                    inputs.validate_pair(self.pair, self.read)
                self.responses[route] = previous

    def test_binding_cannot_reuse_attempt_run_or_controller(self):
        own = self.pair["sources"][0]
        first = inputs.binding(self.pair, own["repository"], 12, 1, own["controllerSha"])
        self.assertNotEqual(first, inputs.binding(self.pair, own["repository"], 12, 2, own["controllerSha"]))
        self.assertNotEqual(first, inputs.binding(self.pair, own["repository"], 13, 1, own["controllerSha"]))
        with self.assertRaises(ValueError):
            inputs.binding(self.pair, own["repository"], 12, 1, "f" * 40)

    def test_default_host_must_be_merged(self):
        sha = "a" * 40
        self.assertEqual(inputs.merged_host(sha, lambda _: {"status": "ahead", "merge_base_commit": {"sha": sha}}), sha)
        for status in ["behind", "diverged"]:
            with self.assertRaises(ValueError):
                inputs.merged_host(sha, lambda _: {"status": status, "merge_base_commit": {"sha": sha}})

    def test_required_accepts_only_current_pair_bound_to_same_attempt(self):
        def module(name):
            spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
            result = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(result)
            return result
        final = module("final-budget")
        required = module("batch-required")
        own = self.pair["sources"][0]
        repo = own["repository"]
        plan = {"repository": "Host", "prNumber": own["prNumber"], "runId": "12", "attempt": "1",
                "headSha": own["headSha"], "baseSha": own["baseSha"], "testedSha": own["testedSha"], "inputPair": self.pair}
        external = f"nxp-budget-v3:1:12:1:22:1:{own['controllerSha']}:{self.pair['pairDigest']}:" + inputs.binding(self.pair, repo, 12, 1, own["controllerSha"])
        self.responses[f"/repos/{repo}/check-suites/10"] = {"app": {"id": 17}}
        self.responses[f"/repos/{repo}/actions/runs/22/attempts/1"] = {
            "id": 22, "run_attempt": 1, "event": "workflow_run", "path": ".github/workflows/final-budget.yml",
            "head_branch": "main", "head_sha": own["controllerSha"], "repository": {"full_name": repo},
            "status": "completed", "conclusion": "success"}
        check = {"external_id": external}
        trusted = SimpleNamespace(ci_inputs=inputs, registration=final.registration, current_check=lambda *args: check)
        audit = SimpleNamespace(api=self.read, job_name=lambda *args: "Host / 完整预算",
                                completed_jobs=lambda *args: [{}], audit=lambda *args: [{"status": "PASS"}])
        self.assertEqual(required.trusted_begin(audit, trusted, repo, plan, {"check_suite_id": 10}), 17)
        for altered in [external.replace(":12:1:", ":12:2:", 1), external[:-64] + "f" * 64,
                        f"nxp-budget-v2:1:12:1:22:1:{own['controllerSha']}"]:
            check["external_id"] = altered
            with self.assertRaises(ValueError):
                required.trusted_begin(audit, trusted, repo, plan, {"check_suite_id": 10})


if __name__ == "__main__":
    unittest.main()
