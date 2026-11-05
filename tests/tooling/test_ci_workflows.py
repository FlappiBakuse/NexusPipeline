from pathlib import Path
import unittest


class CiWorkflowTests(unittest.TestCase):
    def test_preserves_contract(self):
        import yaml
        root = Path(__file__).resolve().parents[2]
        workflow = yaml.load((root / ".github/workflows/ci.yml").read_bytes(), Loader=yaml.BaseLoader)
        self.assertEqual({"pull_request"}, set(workflow["on"]))
        self.assertEqual(["main"], workflow["on"]["pull_request"]["branches"])
        self.assertEqual(["opened", "synchronize", "reopened", "ready_for_review", "edited"],
                         workflow["on"]["pull_request"]["types"])
        self.assertEqual({"contents": "read"}, workflow["permissions"])
        self.assertEqual("true", workflow["concurrency"]["cancel-in-progress"])
        self.assertEqual(1, len(workflow["jobs"]))
        job = next(iter(workflow["jobs"].values()))
        self.assertEqual("Host / 构建检查", job["name"])
        self.assertEqual("10", job["timeout-minutes"])
        self.assertEqual("windows-2025", job["runs-on"])
        approved = {"actions/checkout": "11d5960a326750d5838078e36cf38b85af677262",
                    "actions/setup-python": "a26af69be951a213d495a4c3e4e4022e16d87065",
                    "actions/setup-node": "49933ea5288caeca8642d1e84afbd3f7d6820020",
                    "actions/setup-dotnet": "67a3573c9a986a3f9c594539f4ab511d57bb3ce9",
                    "actions/upload-artifact": "ea165f8d65b6e75b540449e92b4886f43607fa02"}
        for step in job["steps"]:
            if "uses" not in step:
                continue
            action, revision = step["uses"].split("@")
            self.assertEqual(approved[action], revision)
            if action == "actions/checkout":
                self.assertEqual("false", step["with"]["persist-credentials"])
            if action == "actions/upload-artifact":
                self.assertEqual("always()", step["if"])
                self.assertEqual("error", step["with"]["if-no-files-found"])
                self.assertEqual("7", step["with"]["retention-days"])
            if action in ("actions/setup-node", "actions/setup-dotnet"):
                self.assertIn("steps.plan.outputs.", step["if"])
