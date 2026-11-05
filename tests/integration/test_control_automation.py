import hashlib
import json
import time
import unittest
import uuid
import test_host_boundary as host_boundary


class ControlAutomationTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    @staticmethod
    def change(revision, **items):
        return dict(format="nexus.control.automation-change", version=1, operationId=str(uuid.uuid4()),
                    expectedCatalogRevision=revision, roots=items.get("roots", []), scripts=items.get("scripts", []),
                    users=items.get("users", []), bindings=items.get("bindings", []), queues=items.get("queues", []),
                    schedules=items.get("schedules", []), removed=items.get("removed", []))

    def post(self, address, path, command):
        status, data, _ = self.host.secret_request(address, path, json.dumps(command).encode(),
                                                  {"Content-Type": "application/json"})
        return status, json.loads(data) if data else None

    def seed(self, address):
        root, script, user, binding = [str(uuid.uuid4()) for _ in range(4)]
        value = dict(id=script, name="actual-host-version-worker", kind="generic", rootId=root,
                     executable=dict(rootId=root, relative=self.host.executable.name), arguments=["--version"],
                     configurationPaths=[], logPaths=[], adapterResources=[], updateConfigurationAtFinish=False,
                     limits=dict(maximumAttempts=1, stallTimeoutSeconds=30, totalTimeoutSeconds=60),
                     judgement=dict(language="keywords", successKeywords=["0.18.0"], failureKeywords=[]))
        command = self.change(0,
            roots=[dict(id=root, expectedRevision=0, name="actual-build-input", kind="external", path=str(self.host.executable.parent))],
            scripts=[dict(expectedRevision=0, value=value)],
            users=[dict(expectedRevision=0, value=dict(id=user, name="actual-control-user", order=0))],
            bindings=[dict(expectedRevision=0, value=dict(id=binding, userId=user, scriptId=script, enabled=True, priority=0,
                                                         notifyEnabled=False, notificationTaskIds=[]))])
        status, saved = self.post(address, "/control/v1/automation/change", command)
        self.assertEqual((200, 1), (status, saved["catalogRevision"]))
        return command, (root, script, user, binding)

    def test_runs_saved(self):
        """actual definition http start receipt history and original log use the same saved execution."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        command, (_, _, _, binding) = self.seed(address)
        self.assertEqual(200, self.post(address, "/control/v1/automation/change", command)[0])
        status, settings = self.host.request(address, "/control/v1/settings/service")
        self.assertEqual(200, status)
        selection = dict(format="nexus.control.execution-selection", version=1,
                         target=dict(kind="binding", bindingId=binding),
                         expectedCatalogRevision=1, expectedSettingsRevision=json.loads(settings)["revision"])
        before = self.host.durable_state(runtime)
        status, preview = self.post(address, "/control/v1/execution/preview", selection)
        self.assertEqual(200, status)
        self.assertTrue(preview["hasRunnable"])
        self.assertFalse(preview["hasBlockedItems"])
        self.assertEqual(before, self.host.durable_state(runtime))
        operation = str(uuid.uuid4())
        status, accepted = self.post(address, "/control/v1/execution/start/" + operation, selection)
        self.assertEqual(202, status)
        status, replay = self.post(address, "/control/v1/execution/start/" + operation, selection)
        self.assertEqual(202, status)
        self.assertEqual(accepted["runId"], replay["runId"])
        self.assertEqual(accepted["attemptId"], replay["attemptId"])
        deadline = time.monotonic() + 60
        while True:
            status, raw = self.host.request(address, "/control/v1/records?limit=20")
            self.assertEqual(200, status)
            records = json.loads(raw)["items"]
            if any(item["finalAttempt"]["runId"] == accepted["runId"] for item in records):
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.05)
        prefix = "/control/v1/records/" + accepted["runId"] + "/attempts/" + accepted["attemptId"]
        status, raw = self.host.request(address, prefix + "/logs?limit=20")
        self.assertEqual(200, status)
        entries = json.loads(raw)["items"]
        main = next(item for item in entries if item["key"]["role"] == "main")
        key = main["key"]
        path = prefix + "/logs/" + key["stepId"] + "/main"
        status, original = self.host.request(address, path + "/original?snapshotSha256=" + main["snapshotSha256"])
        self.assertEqual(200, status)
        self.assertIn(b"0.18.0", original)
        status, description = self.host.request(address, path + "/description")
        self.assertEqual(200, status)
        described = json.loads(description)
        self.assertEqual(len(original), described["recordedBytes"])
        reconstructed = bytearray()
        for part in described["parts"]:
            status, content = self.host.request(address, path + "/parts/" + str(part["index"]) +
                                                "?snapshotSha256=" + main["snapshotSha256"])
            self.assertEqual(200, status)
            self.assertEqual(part["sha256"], hashlib.sha256(content).hexdigest())
            reconstructed.extend(content)
        self.assertEqual(original, bytes(reconstructed))

    def test_preserves_revisions(self):
        """definition cas exact old replay and cascade delete preserve unedited script revisions."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        _, (_, script, user, binding) = self.seed(address)
        edit = self.change(1, users=[dict(expectedRevision=1, value=dict(id=user, name="renamed", order=1))])
        self.assertEqual(200, self.post(address, "/control/v1/automation/change", edit)[0])
        before = self.host.durable_state(runtime)
        bad = self.change(1, users=[dict(expectedRevision=1, value=dict(id=user, name="stale", order=0))])
        self.assertEqual(409, self.post(address, "/control/v1/automation/change", bad)[0])
        self.assertEqual(before, self.host.durable_state(runtime))
        removal = self.change(2, removed=[dict(kind="user", id=user, expectedRevision=2)])
        self.assertEqual(200, self.post(address, "/control/v1/automation/change", removal)[0])
        self.assertEqual(200, self.post(address, "/control/v1/automation/change", edit)[0])
        status, raw = self.host.request(address, "/control/v1/automation")
        self.assertEqual(200, status)
        saved = json.loads(raw)
        self.assertEqual(3, saved["catalogRevision"])
        self.assertEqual([], saved["bundle"]["users"])
        self.assertEqual([], saved["bundle"]["bindings"])
        self.assertEqual(1, next(item["revision"] for item in saved["revisions"] if item["kind"] == "script" and item["id"] == script))
        before = self.host.durable_state(runtime)
        self.assertEqual(400, self.post(address, "/control/v1/automation/change", self.change(3) | dict(subjectId="forged"))[0])
        self.assertEqual(before, self.host.durable_state(runtime))
