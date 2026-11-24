import contextlib
import http.client
import json
import os
import re
import socket
import time
import subprocess
import unittest
import urllib.error
import urllib.parse
import urllib.request
import uuid
import queue
import threading
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
        self.assertEqual("ready", self.get(started["address"], "/control/v1/status")["admission"])

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
        stream = address + f'/control/v1/events?streamId={snap["streamId"]}&afterSequence={snap["sequence"] - 1}'
        with urllib.request.urlopen(stream, timeout=10) as connected:
            self.assertEqual("text/event-stream", connected.headers.get_content_type())
            for _ in range(8):
                line = connected.readline()
                if line.startswith(b"data: "):
                    state_event = json.loads(line[6:])
                    break
            else:
                self.fail("No state event returned for the snapshot revision")
        self.assertEqual("state", state_event["kind"])
        self.assertEqual(snap["sequence"], state_event["sequence"])
        self.assertEqual(snap["state"], state_event["data"])
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
        address, operation, accepted, selection, deadline = self.start_waiting_queue("shutdown")
        self.cancel_waiting_queue(address, operation, accepted)
        while True:
            value = self.get(address, "/control/v1/operations/" + operation)
            if value["status"] == "completed":
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.05)
        status, _ = self.host.request(address, "/control/v1/status")
        self.assertEqual(200, status)
        self.assertEqual(202, self.post(address, "/control/v1/execution/start/" + operation, selection)[0])

    def start_waiting_queue(self, completion_action):
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
                                                       completionAction=completion_action, notifyEnabled=False, notificationTaskIds=[]))])
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
        return address, operation, accepted, selection, deadline

    def cancel_waiting_queue(self, address, operation, accepted):
        cancel_id = str(uuid.uuid4())
        cancellation = dict(format="nexus.control.execution-cancellation", version=1,
                            runId=accepted["runId"], attemptId=accepted["attemptId"])
        status, cancelled = self.post(address, "/control/v1/execution/" + operation + "/cancel/" + cancel_id, cancellation)
        self.assertEqual(202, status)
        self.assertEqual("dispatch-closed", cancelled["acknowledgement"])

    def open_control_stream(self, address, path, stack):
        try:
            response = urllib.request.urlopen(address + path, timeout=10)
        except urllib.error.HTTPError as error:
            with error:
                self.assertEqual(503, error.code, path)
                self.assertEqual("control_busy", json.load(error)["code"])
                return None
        self.assertEqual("text/event-stream", response.headers.get_content_type())
        return stack.enter_context(response)

    def control_stream_paths(self, address, accepted):
        snap = self.get(address, "/control/v1/events/snapshot")
        active = self.get(address, "/control/v1/execution/active")["items"]
        current = next(row for row in active if row["runId"] == accepted["runId"])
        step = urllib.parse.quote(current["steps"][0]["stepId"], safe="")
        event = f'/control/v1/events?streamId={snap["streamId"]}&afterSequence={snap["sequence"]}'
        log = f'/control/v1/execution/runs/{accepted["runId"]}/attempts/{accepted["attemptId"]}/logs/{step}/main/events'
        return event, log

    def save_control_preferences(self, address):
        path = "/control/v1/settings/interface"
        before = self.get(address, path)
        values = {key: value for key, value in before["values"].items() if value is not None}
        values["theme"] = "light" if values["theme"] == "dark" else "dark"
        status, saved = self.post(address, path + "/change", dict(format="nexus.control.interface-change", version=1,
            operationId=str(uuid.uuid4()), expectedRevision=before["revision"], values=values))
        self.assertEqual(200, status, saved)
        self.assertEqual(before["revision"] + 1, saved["revision"])
        self.assertEqual(saved, self.get(address, path))

    def test_keeps_controls_available_under_long_streams(self):
        """Real status subscriptions and live logs leave room to read, save and cancel on Kestrel."""
        address, operation, accepted, _, _ = self.start_waiting_queue("none")
        event_path, log_path = self.control_stream_paths(address, accepted)
        with contextlib.ExitStack() as streams:
            logs = [self.open_control_stream(address, log_path, streams) for _ in range(8)]
            events = [self.open_control_stream(address, event_path, streams) for _ in range(24)]
            status, response = self.host.request(address, "/control/v1/status")
            self.assertEqual(200, status, response)
            self.assertEqual(8, sum(response is not None for response in logs))
            connected = [response for response in events if response is not None]
            self.assertEqual(8, len(connected))
            self.assertIsNone(self.open_control_stream(address, log_path, streams))
            for path, response in ((event_path, connected[0]), (log_path, logs[0])):
                response.close()
                deadline = time.monotonic() + 5
                while self.open_control_stream(address, path, streams) is None:
                    self.assertLess(time.monotonic(), deadline, path)
                    time.sleep(0.025)
            self.save_control_preferences(address)
            self.cancel_waiting_queue(address, operation, accepted)
            self.assertEqual(200, self.host.request(address, "/control/v1/status")[0])

    def test_keeps_controls_available_during_asset_downloads(self):
        """Clients that hold embedded asset response bodies leave control and cancellation available."""
        address, operation, accepted, _, _ = self.start_waiting_queue("none")
        status, page = self.host.request(address, "/")
        self.assertEqual(200, status)
        entry = re.search(rb'src="(/assets/[^"\r\n]+\.js)"', page)
        self.assertIsNotNone(entry)
        peer = urllib.parse.urlsplit(address)
        request = b"GET " + entry[1] + f" HTTP/1.1\r\nHost: {peer.netloc}\r\nConnection: close\r\n\r\n".encode("ascii")
        with contextlib.ExitStack() as downloads:
            attempts = []
            for _ in range(16):
                client = downloads.enter_context(socket.socket(socket.AF_INET, socket.SOCK_STREAM))
                client.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024)
                client.settimeout(8)
                client.connect((peer.hostname, peer.port))
                client.sendall(request)
                response = downloads.enter_context(http.client.HTTPResponse(client))
                response.begin()
                attempts.append(response.status)
                if response.status == 200:
                    self.assertGreater(int(response.getheader("Content-Length")), 1024 * 1024)
                else:
                    self.assertEqual(503, response.status)
                    self.assertEqual("control_busy", json.load(response)["code"])
            status, body = self.host.request(address, "/control/v1/status")
            self.assertEqual(200, status, body)
            self.assertEqual(8, attempts.count(200))
            self.assertEqual(8, attempts.count(503))
            self.save_control_preferences(address)
            self.cancel_waiting_queue(address, operation, accepted)
        deadline = time.monotonic() + 5
        while True:
            status, body = self.host.request(address, entry[1].decode("ascii"))
            if status == 200:
                break
            self.assertEqual(503, status, body)
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.025)

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
