from __future__ import annotations

import json
import unittest

from tools.qualification_contract import ContractError, parse_strict_json, validate_jobs, validate_proof


H = "a" * 40
B = "b" * 40
D = "d" * 40


def proof(repository: str) -> dict:
    return {
        "schemaVersion": 1,
        "repository": repository,
        "headSha": H,
        "baseSha": B,
        "workflowSha": B,
        "runId": 12,
        "runAttempt": 2,
        "appId": 17,
        "sdkSourceSha": "" if repository.endswith("NexusPipeline") else D,
        "contractSourceSha": D,
        "gates": {name: "success" for name in ("H1", "H2", "H3", "H4", "H5")},
    }


class QualificationContractTests(unittest.TestCase):
    def test_valid_host_proof(self) -> None:
        value = proof("FlappiBakuse/NexusPipeline")
        validate_proof(value, repository=value["repository"], head_sha=H, base_sha=B, workflow_sha=B, run_id=12, run_attempt=2, app_id=17, gate_names=("H1", "H2", "H3", "H4", "H5"), sdk_source_sha="", contract_source_sha=D)

    def test_duplicate_unknown_bool_and_prefix_are_rejected(self) -> None:
        with self.assertRaises(ContractError):
            parse_strict_json('{"runId": 12, "runId": 123}')
        value = proof("FlappiBakuse/NexusPipeline")
        for changed in ({**value, "trusted": True}, {**value, "runId": True}, {**value, "runId": 1234}):
            with self.subTest(changed=changed), self.assertRaises(ContractError):
                validate_proof(changed, repository=value["repository"], head_sha=H, base_sha=B, workflow_sha=B, run_id=12, run_attempt=2, app_id=17, gate_names=value["gates"], sdk_source_sha="", contract_source_sha=D)

    def test_skipped_execution_step_is_not_success(self) -> None:
        jobs = [{"name": "H1", "run_id": 12, "status": "completed", "conclusion": "success", "steps": [{"name": "Execute gate", "status": "completed", "conclusion": "skipped"}]}]
        with self.assertRaises(ContractError):
            validate_jobs(jobs, request_run_id=12, request_attempt=2, gate_names=("H1",))


if __name__ == "__main__":
    unittest.main()
