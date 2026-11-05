import hashlib
import json
import os
import time
import subprocess
import unittest
import uuid
import urllib.parse
import queue
import threading
import sqlite3
import contextlib
import test_host_boundary as host_boundary
import test_control_automation as automation_control


class ControlCompositionTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def post(self, address, path, command):
        status, data, _ = self.host.secret_request(
            address, path, json.dumps(command).encode(), {"Content-Type": "application/json"})
        return status, json.loads(data) if data else None

    def get(self, address, path):
        status, raw = self.host.request(address, path)
        self.assertEqual(200, status, raw)
        return json.loads(raw)

    def test_filters_logs(self):
        """saved log level filters actual host logs live without mixing machine stdout."""
        runtime = self.host.scope / "runtime"
        process, started = self.host.start(runtime)
        observed = queue.Queue()

        def collect():
            for line in process.stderr:
                if "Host settings saved" in line:
                    observed.put(line)

        reader = threading.Thread(target=collect, daemon=True)
        reader.start()
        deadline = time.monotonic() + 10
        while True:
            with contextlib.closing(sqlite3.connect((runtime / "state/nexus.db").as_uri() + "?mode=ro", uri=True)) as database:
                if database.execute("SELECT count(*) FROM schedule_minute_claims;").fetchone()[0]:
                    break
            self.assertLess(time.monotonic(), deadline, "Initial scheduling did not complete")
            time.sleep(0.02)

        def save(level):
            current = self.get(started["address"], "/control/v1/settings")["saved"]
            command = dict(format="nexus.control.host-settings", version=1, operationId=str(uuid.uuid4()),
                           expectedRevision=current["revision"], values=current["values"] | dict(logLevel=level))
            status, result = self.post(started["address"], "/control/v1/settings", command)
            self.assertEqual(200, status, result)

        save("fatal")
        save("info")
        self.assertIn("Host settings saved", observed.get(timeout=5))
        save("error")
        time.sleep(0.2)
        process.terminate()
        process.wait(timeout=15)
        reader.join(timeout=3)
        self.assertFalse(reader.is_alive())
        self.assertTrue(observed.empty(), "Disabled information logs were emitted")

    def test_preserves_preferences(self):
        """complete settings use original cas and old replay does not rewind current preferences."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        before = self.get(address, "/control/v1/settings")
        values = before["saved"]["values"] | dict(historyRetentionDays=21, locale="enUs")
        operation = str(uuid.uuid4())
        command = dict(format="nexus.control.host-settings", version=1, operationId=operation,
                       expectedRevision=before["saved"]["revision"], values=values)
        status, saved = self.post(address, "/control/v1/settings", command)
        self.assertEqual(200, status)
        self.assertEqual(21, saved["saved"]["values"]["historyRetentionDays"])
        next_command = command | dict(operationId=str(uuid.uuid4()), expectedRevision=saved["saved"]["revision"],
                                     values=values | dict(historyRetentionDays=22))
        self.assertEqual(200, self.post(address, "/control/v1/settings", next_command)[0])
        self.assertEqual(200, self.post(address, "/control/v1/settings", command)[0])
        current = self.get(address, "/control/v1/settings")
        self.assertEqual(22, current["saved"]["values"]["historyRetentionDays"])
        snapshot = self.host.durable_state(runtime)
        self.assertEqual(409, self.post(address, "/control/v1/settings", command | dict(values=values | dict(locale="zhCn")))[0])
        self.assertEqual(snapshot, self.host.durable_state(runtime))
        self.assertFalse(current["saved"]["values"]["autoStart"])

    def test_shares_definitions(self):
        """actual template export import and cli unique name resolution share current definition owners."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        helper = automation_control.ControlAutomationTests(methodName="runTest")
        helper.host = self.host
        _, (_, script, _, _) = helper.seed(address)
        exported = self.get(address, "/control/v1/automation/export?revision=1&templateScriptId=" + script)
        self.assertEqual("scriptTemplate", exported["bundle"]["exportKind"])
        target_runtime = self.host.scope / "target"
        _, target = self.host.start(target_runtime)
        roots = [dict(id=item["id"], path=str(self.host.executable.parent)) for item in exported["bundle"]["roots"]]
        command = dict(format="nexus.control.automation-import", version=1, operationId=str(uuid.uuid4()),
                       expectedRevision=0, action="preview", previewDigest="0" * 64,
                       bundle=exported["bundle"], roots=roots, attachments=[])
        status, preview = self.post(target["address"], "/control/v1/automation/import/preview", command)
        self.assertEqual(200, status)
        self.assertTrue(preview["canApply"])
        apply = command | dict(action="apply", previewDigest=preview["previewDigest"])
        self.assertEqual(202, self.post(target["address"], "/control/v1/automation/import/apply", apply)[0])
        self.assertEqual(202, self.post(target["address"], "/control/v1/automation/import/apply", apply)[0])
        saved = self.get(target["address"], "/control/v1/automation")
        self.assertEqual(1, len(saved["bundle"]["scripts"]))
        name = saved["bundle"]["scripts"][0]["name"]
        command_line = [str(self.host.executable), "control", "resolve", "--address", target["address"],
                        "--kind", "script", "--value", name]
        result = subprocess.run(command_line, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=30,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(0, result.returncode, result.stderr)
        envelope = json.loads(result.stdout)
        self.assertEqual(200, envelope["status"])
        self.assertEqual(saved["bundle"]["scripts"][0]["id"], envelope["body"]["id"])
        resolved = self.get(target["address"], "/control/v1/automation/resolve?kind=script&value=" + envelope["body"]["id"])
        self.assertEqual(envelope["body"], resolved)

    def test_preserves_authority(self):
        """actual host snapshot resync and missing media owner never create authority or durable effects."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        snap = self.get(address, "/control/v1/events/snapshot")
        self.assertGreater(snap["sequence"], 0)
        self.assertIn("completedRuns", snap["state"])
        status, raw = self.host.request(address, "/control/v1/events?streamId=" + str(uuid.uuid4()) + "&afterSequence=0")
        self.assertEqual(200, status)
        data = next(line[6:] for line in raw.decode().splitlines() if line.startswith("data: "))
        event = json.loads(data)
        self.assertEqual("nexus.event", event["format"])
        self.assertEqual("resync", event["kind"])
        self.assertEqual(snap["streamId"], event["streamId"])
        self.assertTrue(event["data"]["requiresSnapshot"])
        before = self.host.durable_state(runtime)
        forged = dict(format="nexus.control.media-grant", version=1, runId=str(uuid.uuid4()),
                      attemptId=str(uuid.uuid4()), extensionId="missing-extension", providerId="missing-provider")
        status, denied = self.post(address, "/control/v1/media/previews", forged)
        self.assertEqual(403, status)
        self.assertEqual("access_denied", denied["code"])
        self.assertEqual(before, self.host.durable_state(runtime))
        self.assertEqual([], self.get(address, "/control/v1/extensions")["items"])
        status, _ = self.host.request(address, "/control/v1/extensions/operations/" + str(uuid.uuid4()))
        self.assertEqual(404, status)

    def test_cancels_queue(self):
        """actual local queue with a completion action uses original local authorization then cancels without system effects."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        helper = automation_control.ControlAutomationTests(methodName="runTest")
        helper.host = self.host
        _, (root_id, script_id, _, binding) = helper.seed(address)
        executable = __import__("pathlib").Path(os.environ["NEXUS_PROCESS_FIXTURE_EXE"])
        original = self.get(address, "/control/v1/automation")
        script = next(item for item in original["bundle"]["scripts"] if item["id"] == script_id)
        marker = self.host.scope / "running-editor-free-worker"
        script = script | dict(executable=dict(rootId=root_id, relative=executable.name),
                               arguments=["process-output", "wait", str(marker)],
                               limits=dict(maximumAttempts=1, stallTimeoutSeconds=120, totalTimeoutSeconds=120),
                               judgement=dict(language="keywords", successKeywords=["never-complete"], failureKeywords=[]))
        queue_id = str(uuid.uuid4())
        change = helper.change(1,
            roots=[dict(id=root_id, expectedRevision=1, name="actual-owned-worker", kind="external", path=str(executable.parent))],
            scripts=[dict(expectedRevision=1, value=script)],
            queues=[dict(expectedRevision=0, value=dict(id=queue_id, name="local-completion-origin", scriptIds=[script["id"]], order=0,
                                                       completionAction="shutdown", notifyEnabled=False, notificationTaskIds=[]))])
        self.assertEqual(200, self.post(address, "/control/v1/automation/change", change)[0])
        settings = self.get(address, "/control/v1/settings/service")
        selection = dict(format="nexus.control.execution-selection", version=1, target=dict(kind="queue", queueId=queue_id),
                         expectedCatalogRevision=2, expectedSettingsRevision=settings["revision"])
        operation = str(uuid.uuid4())
        status, accepted = self.post(address, "/control/v1/execution/start/" + operation, selection)
        self.assertEqual(202, status)
        ready = self.host.native_path(marker.with_name(marker.name + ".output-ready"))
        deadline = time.monotonic() + 45
        while not ready.exists():
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.02)
        cancel_id = str(uuid.uuid4())
        cancellation = dict(format="nexus.control.execution-cancellation", version=1,
                            runId=accepted["runId"], attemptId=accepted["attemptId"])
        status, cancelled = self.post(address, "/control/v1/execution/" + operation + "/cancel/" + cancel_id, cancellation)
        self.assertEqual(202, status)
        self.assertEqual("dispatch-closed", cancelled["acknowledgement"])
        while True:
            value = self.get(address, "/control/v1/operations/" + operation)
            if value["status"] == "completed":
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.05)
        status, _ = self.host.request(address, "/control/v1/status")
        self.assertEqual(200, status)
        self.assertEqual(202, self.post(address, "/control/v1/execution/start/" + operation, selection)[0])

    def test_selects_binary(self):
        """cli binary selection is explicit and never becomes a generic url or file writer."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        before = self.host.durable_state(runtime)
        for arguments in [
            ["media-frame", "--preview-id", str(uuid.uuid4())],
            ["get", "--url", "/control/v1/settings"],
            ["settings", "--output", str(runtime / "must-not-exist")],
        ]:
            result = subprocess.run([str(self.host.executable), "control", arguments[0], "--address", address, *arguments[1:]],
                                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15,
                                    creationflags=subprocess.CREATE_NO_WINDOW)
            self.assertEqual(2, result.returncode)
            self.assertEqual("nexus.control.cli-response", json.loads(result.stdout)["format"])
        command_line = [str(self.host.executable), "control", "media-frame", "--address", address,
                        "--preview-id", str(uuid.uuid4()), "--binary-stdout", "true"]
        rejected = subprocess.run(command_line, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=15,
                                  creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(4, rejected.returncode)
        self.assertEqual(b"", rejected.stdout)
        self.assertEqual(before, self.host.durable_state(runtime))


if __name__ == "__main__":
    unittest.main()
