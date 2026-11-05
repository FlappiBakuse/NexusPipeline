import http.server
import json
import threading
import unittest
import uuid
import test_host_boundary as host_boundary


class ControlNotificationTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def command(self, action, revision, **values):
        return dict(format="nexus.control.notification-command", version=1, action=action,
                    operationId=str(uuid.uuid4()), expectedCatalogRevision=revision, **values)

    def call(self, address, command, route=None):
        action = route or command["action"]
        status, body, _ = self.host.secret_request(
            address, "/control/v1/notifications/" + action, json.dumps(command).encode("utf-8"),
            {"Content-Type": "application/json; charset=utf-8"})
        return status, json.loads(body)

    @staticmethod
    def task(name):
        return dict(name=name, remark="", kind="webhook", isEnabled=True, configuration=dict(
            screenshotEnabled=False, timeoutSeconds=5, webhookKind="generic", webhookTemplate='{"text":{text}}',
            feishuAppId="", slackChannelId="", smtpHost="", smtpPort=465, smtpSecurity="auto",
            smtpUser="", smtpFrom="", smtpTo="", smtpSubjectPrefix="[NexusPipeline]"))

    def test_sends_saved(self):
        """actual http catalog secret cas reorder and saved task test reach only the local sink."""
        arrived = threading.Event()
        requests = []

        class Sink(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length", "-1"))
                if length < 0 or length > 1048576:
                    self.send_error(413)
                    return
                requests.append((self.path, self.rfile.read(length)))
                self.send_response(200)
                self.send_header("Content-Length", "0")
                self.end_headers()
                arrived.set()

            def log_message(self, *_):
                pass

        with http.server.ThreadingHTTPServer(("127.0.0.1", 0), Sink) as sink:
            thread = threading.Thread(target=sink.serve_forever, daemon=True)
            thread.start()
            try:
                runtime = self.host.scope / "runtime"
                process, identity = self.host.start(runtime)
                address = identity["address"]
                task = str(uuid.uuid4())
                target = "http://127.0.0.1:" + str(sink.server_port) + "/saved-only"
                saved = self.command("save", 0, taskId=task, expectedTaskRevision=0, input=self.task("first"),
                                     secrets=[dict(field="webhook-url", expectedRevision=0, disposition="set", value=target)])
                status, catalog = self.call(address, saved)
                self.assertEqual((200, 1), (status, catalog["revision"]))
                self.assertNotIn(target, json.dumps(catalog))
                self.assertEqual(catalog, self.call(address, saved)[1])
                other = str(uuid.uuid4())
                status, _ = self.call(address, self.command("save", 1, taskId=other, expectedTaskRevision=0,
                                                            input=self.task("second"), secrets=[]))
                self.assertEqual(200, status)
                status, ordered = self.call(address, self.command("reorder", 2, order=[other, task]))
                self.assertEqual(200, status)
                self.assertEqual([other, task], [item["id"] for item in ordered["tasks"]])
                before = self.host.durable_state(runtime)
                stale = self.command("save", 1, taskId=task, expectedTaskRevision=1, input=self.task("changed"), secrets=[])
                self.assertEqual(409, self.call(address, stale)[0])
                self.assertEqual(before, self.host.durable_state(runtime))
                test = self.command("test", 3, taskId=task, expectedTaskRevision=1)
                status, accepted = self.call(address, test)
                self.assertEqual(202, status)
                self.assertEqual(test["operationId"], accepted["operationId"])
                self.assertTrue(arrived.wait(15))
                self.assertEqual("/saved-only", requests[0][0])
                self.assertEqual(202, self.call(address, test)[0])
                self.assertEqual(1, len(requests))
                changed = self.command("save", 3, taskId=task, expectedTaskRevision=1, input=self.task("first"),
                                       secrets=[dict(field="webhook-url", expectedRevision=1, disposition="clear")])
                self.assertEqual(200, self.call(address, changed)[0])
                status, data = self.host.request(address, "/control/v1/notifications/" + task + "/credentials")
                self.assertEqual(200, status)
                fields = json.loads(data)["fields"]
                self.assertFalse(next(item for item in fields if item["field"] == "webhook-url")["isConfigured"])
                self.assertEqual(200, self.call(address, self.command("delete", 4, taskId=other, expectedTaskRevision=1))[0])
                self.assertEqual(200, self.call(address, self.command("limit", 5, maximum=1))[0])
                process.terminate()
                process.wait(timeout=15)
                self.host.assert_secret_absent(runtime, target)
            finally:
                sink.shutdown()
                thread.join(timeout=5)

    def test_rejects_rebinding(self):
        """unknown fields action rebinding and unsaved task test never publish a delivery."""
        runtime = self.host.scope / "runtime"
        _, identity = self.host.start(runtime)
        address = identity["address"]
        baseline = self.host.durable_state(runtime)
        task = str(uuid.uuid4())
        commands = [
            (self.command("save", 0, taskId=task, expectedTaskRevision=0, input=self.task("invalid"), subjectId="local:other"), None),
            (self.command("test", 0, taskId=task, expectedTaskRevision=0), None),
            (self.command("test", 0, taskId=task, expectedTaskRevision=0), "save"),
            (self.command("save", 0, taskId=task, expectedTaskRevision=0, input=self.task("invalid"),
                          secrets=[dict(field="control-bearer", expectedRevision=0, disposition="set", value="forbidden")]), None),
            (self.command("save", 0, taskId=task, expectedTaskRevision=0, input=self.task("invalid"),
                          secrets=[dict(field="webhook-url", expectedRevision=0, disposition="clear", value="forbidden")]), None),
        ]
        for command, route in commands:
            with self.subTest(action=command["action"], route=route):
                self.assertIn(self.call(address, command, route)[0], (400, 409))
                self.assertEqual(baseline, self.host.durable_state(runtime))


if __name__ == "__main__":
    unittest.main()
