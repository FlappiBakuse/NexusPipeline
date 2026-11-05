import http.server
import json
import subprocess
import threading
import unittest
import uuid
import test_host_boundary as host_boundary
import test_control_notifications as notification_control


class ControlCliTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def cli(self, address, command, *arguments, body=None):
        result = subprocess.run([str(self.host.executable), "control", command, "--address", address, *arguments],
                                input=body, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=75,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        reply = json.loads(result.stdout)
        self.assertEqual("nexus.control.cli-response", reply["format"])
        self.assertEqual(1, reply["version"])
        return result, reply

    def menu(self, address, choices):
        return subprocess.run([str(self.host.executable), "control", "menu", "--address", address],
                              input=choices.encode("utf-8"), capture_output=True, timeout=30,
                              creationflags=subprocess.CREATE_NO_WINDOW)

    def test_uses_owners(self):
        """resident cli uses actual notification http owners exact operations and local sink."""
        arrived = threading.Event()
        requests = []

        class Sink(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                length = int(self.headers.get("Content-Length", "-1"))
                if not 0 <= length <= 1048576:
                    self.send_error(413)
                    return
                requests.append(self.rfile.read(length))
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
                _, started = self.host.start(self.host.scope / "runtime")
                address = started["address"]
                helper = notification_control.ControlNotificationTests(methodName="runTest")
                task = str(uuid.uuid4())
                target = "http://127.0.0.1:" + str(sink.server_port) + "/cli-exact-saved"
                save = helper.command("save", 0, taskId=task, expectedTaskRevision=0, input=helper.task("cli"),
                                      secrets=[dict(field="webhook-url", expectedRevision=0, disposition="set", value=target)])
                payload = json.dumps(save).encode()
                result, saved = self.cli(address, "notification-save", "--input", "-", body=payload)
                self.assertEqual(0, result.returncode)
                self.assertEqual(200, saved["status"])
                self.assertEqual(save["operationId"], saved["operationId"])
                self.assertNotIn(target.encode(), result.stdout + result.stderr)
                _, replay = self.cli(address, "notification-save", "--input", "-", body=payload)
                self.assertEqual(saved["body"], replay["body"])
                conflict = save | dict(input=helper.task("changed"))
                failed, reply = self.cli(address, "notification-save", "--input", "-", body=json.dumps(conflict).encode())
                self.assertEqual(4, failed.returncode)
                self.assertEqual(409, reply["status"])
                request = helper.command("test", 1, taskId=task, expectedTaskRevision=1)
                result, accepted = self.cli(address, "notification-test", "--input", "-", body=json.dumps(request).encode())
                self.assertEqual((0, 202), (result.returncode, accepted["status"]))
                self.assertTrue(arrived.wait(20))
                self.cli(address, "notification-test", "--input", "-", body=json.dumps(request).encode())
                self.assertEqual(1, len(requests))
                result, queried = self.cli(address, "operation", "--id", request["operationId"])
                self.assertEqual(0, result.returncode)
                self.assertEqual(request["operationId"], queried["body"]["operationId"])
                result, catalog = self.cli(address, "notifications")
                self.assertEqual(saved["body"]["tasks"], catalog["body"]["tasks"])
                source = self.host.scope / "通知-编辑.json"
                source.write_bytes(payload)
                menu = self.menu(address, "5\n4\n" + str(source) + "\nyes\n3\n0\n1\n3\n0\n0\n")
                self.assertEqual(0, menu.returncode, menu.stderr.decode("utf-8"))
                replies = [json.loads(line) for line in menu.stdout.splitlines()]
                self.assertEqual(3, len(replies))
                self.assertEqual(saved["body"], replies[0]["body"])
                self.assertEqual(catalog["body"], replies[1]["body"])
                self.assertEqual("nexus.runtime.diagnostic", replies[2]["body"]["format"])
                self.assertNotIn(target.encode(), menu.stdout + menu.stderr)
            finally:
                sink.shutdown()
                thread.join(timeout=10)

    def test_rejects_options(self):
        """cli rejects universal routes duplicate input and runtime mutation options without echoing bytes."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        before = self.host.durable_state(runtime)
        for command, arguments, body in (
            ("api", ("--path", "/control/v1/notifications"), None),
            ("status", ("--runtime-root", str(runtime)), None),
            ("notification-save", ("--input", "-"), b'{"secret":"never-echo-this","secret":"again"}'),
            ("execution-start", ("--input", "-"), b"{}"),
        ):
            with self.subTest(command=command):
                result, reply = self.cli(address, command, *arguments, body=body)
                self.assertEqual(2, result.returncode)
                self.assertEqual("control_cli_input_invalid", reply["code"])
                self.assertNotIn(b"never-echo-this", result.stdout + result.stderr)
                self.assertEqual(before, self.host.durable_state(runtime))
        invalid = self.host.scope / "invalid.json"
        invalid.write_bytes(b'{"secret":"never-echo-this","secret":"again"}')
        menu = self.menu(address, "invalid\n5\n2\n\n2\n-\n2\n" + str(invalid) + "\nno\n2\n" +
                         str(invalid) + "\nyes\n0\n1\n4\ninvalid-id\n0\n0\n")
        self.assertEqual(0, menu.returncode)
        replies = [json.loads(line) for line in menu.stdout.splitlines()]
        self.assertEqual(2, len(replies))
        self.assertTrue(all(reply["code"] == "control_cli_input_invalid" for reply in replies))
        self.assertIn(b"Invalid choice", menu.stderr)
        self.assertIn(b"Use a file", menu.stderr)
        self.assertNotIn(b"never-echo-this", menu.stdout + menu.stderr)
        self.assertEqual(before, self.host.durable_state(runtime))
