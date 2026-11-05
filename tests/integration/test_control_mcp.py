import contextlib
import http.server
import json
import socket
import threading
import unittest
import uuid
import test_host_boundary as host_boundary
import test_control_notifications as notification_control


class ControlMcpTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def service(self, address, revision, values, operation=None):
        command = dict(format="nexus.control.service-settings", version=1, operationId=operation or str(uuid.uuid4()),
                       expectedRevision=revision, service=values)
        status, body, _ = self.host.secret_request(
            address, "/control/v1/settings/service", json.dumps(command).encode(),
            {"Content-Type": "application/json; charset=utf-8"})
        self.assertEqual(200, status, body)
        return json.loads(body)

    def enable(self, write=False):
        runtime = self.host.scope / "runtime"
        process, first = self.host.start(runtime)
        status, body = self.host.request(first["address"], "/control/v1/settings/service")
        self.assertEqual(200, status)
        saved = json.loads(body)
        self.assertFalse(saved["service"]["mcpEnabled"])
        self.assertFalse(saved["service"]["mcpWriteEnabled"])
        with contextlib.closing(socket.socket()) as reservation:
            reservation.bind(("127.0.0.1", 0))
            port = reservation.getsockname()[1]
        values = saved["service"] | dict(mcpEnabled=True, mcpWriteEnabled=write, mcpPort=port)
        self.service(first["address"], saved["revision"], values)
        process.terminate()
        process.wait(timeout=15)
        _, started = self.host.start(runtime)
        self.address = started["address"]
        self.mcp = "http://127.0.0.1:" + str(port)
        self.values = values
        return runtime

    def rpc(self, method, parameters=None, session=None, identity=1, raw=None, version="2025-11-25"):
        command = dict(jsonrpc="2.0", method=method)
        if identity is not None:
            command["id"] = identity
        if parameters is not None:
            command["params"] = parameters
        headers = {"Content-Type": "application/json; charset=utf-8", "Accept": "application/json, text/event-stream"}
        if session:
            headers["Mcp-Session-Id"] = session
            headers["MCP-Protocol-Version"] = version
        status, body, reply = self.host.secret_request(self.mcp, "/control/v1/mcp",
                                                       raw if raw is not None else json.dumps(command).encode(), headers)
        return status, json.loads(body) if body else None, {key.lower(): value for key, value in reply.items()}

    def initialize(self):
        status, body, headers = self.rpc("initialize", dict(protocolVersion="2025-11-25",
                                        capabilities={}, clientInfo=dict(name="actual-local-fixture", version="1")))
        self.assertEqual(200, status)
        self.assertEqual("2025-11-25", body["result"]["protocolVersion"])
        session = headers["mcp-session-id"]
        self.assertEqual((202, None), self.rpc("notifications/initialized", session=session, identity=None)[:2])
        return session

    def test_rechecks_switches(self):
        """actual mcp hides writes and rechecks saved switches for cached calls without client subjects."""
        runtime = self.enable()
        session = self.initialize()
        status, listed, _ = self.rpc("tools/list", session=session)
        self.assertEqual(200, status)
        self.assertEqual({"notifications_list", "status", "doctor", "records_list", "record_attempts",
                          "record_logs", "record_log_tail", "record_images", "execution_preview", "automation_list", "scheduling_list", "configuration_resources", "operation_read", "capabilities"},
                         {tool["name"] for tool in listed["result"]["tools"]})
        helper = notification_control.ControlNotificationTests(methodName="runTest")
        task = str(uuid.uuid4())
        command = helper.command("save", 0, taskId=task, expectedTaskRevision=0, input=helper.task("mcp"), secrets=[])
        before = self.host.durable_state(runtime)
        _, denied, _ = self.rpc("tools/call", dict(name="notifications_save", arguments=command), session)
        self.assertTrue(denied["result"]["isError"])
        self.assertEqual(before, self.host.durable_state(runtime))
        _, execution_denied, _ = self.rpc("tools/call", dict(name="execution_start", arguments={}), session)
        self.assertTrue(execution_denied["result"]["isError"])
        self.assertEqual(before, self.host.durable_state(runtime))
        self.values["mcpWriteEnabled"] = True
        self.service(self.address, 1, self.values)
        _, listed, _ = self.rpc("tools/list", session=session)
        names = [tool["name"] for tool in listed["result"]["tools"]]
        self.assertEqual(21, len(names))
        self.assertNotIn("shell", names)
        _, changed, _ = self.rpc("tools/call", dict(name="notifications_save", arguments=command), session)
        self.assertFalse(changed["result"]["isError"])
        self.assertEqual(1, changed["result"]["structuredContent"]["revision"])
        status, data, _ = self.host.secret_request(self.address, "/control/v1/notifications/save",
            json.dumps(command).encode(), {"Content-Type": "application/json; charset=utf-8"})
        self.assertEqual(200, status)
        self.assertEqual(1, json.loads(data)["revision"])
        before = self.host.durable_state(runtime)
        _, unknown, _ = self.rpc("tools/call", dict(name="delete_files", arguments={}), session)
        self.assertEqual(-32602, unknown["error"]["code"])
        self.assertEqual(before, self.host.durable_state(runtime))
        self.values["mcpEnabled"] = False
        self.service(self.address, 2, self.values)
        self.assertEqual(403, self.rpc("tools/call", dict(name="notifications_list", arguments={}), session)[0])
        self.assertEqual(404, self.host.secret_request(self.address, "/control/v1/mcp", b"{}",
                                                     {"Content-Type": "application/json"})[0])

    def test_tests_task(self):
        """actual mcp saved task test uses the shared source and sends only once to a local socket."""
        self.enable(write=True)
        session = self.initialize()
        arrived = threading.Event()
        paths = []

        class Sink(http.server.BaseHTTPRequestHandler):
            def do_POST(self):
                count = int(self.headers.get("Content-Length", "-1"))
                if count < 0 or count > 1048576:
                    self.send_error(413)
                    return
                self.rfile.read(count)
                paths.append(self.path)
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
                helper = notification_control.ControlNotificationTests(methodName="runTest")
                task = str(uuid.uuid4())
                target = "http://127.0.0.1:" + str(sink.server_port) + "/mcp-saved"
                command = helper.command("save", 0, taskId=task, expectedTaskRevision=0, input=helper.task("socket"),
                                         secrets=[dict(field="webhook-url", expectedRevision=0, disposition="set", value=target)])
                _, saved, _ = self.rpc("tools/call", dict(name="notifications_save", arguments=command), session)
                self.assertFalse(saved["result"]["isError"])
                self.assertNotIn(target, json.dumps(saved))
                test = helper.command("test", 1, taskId=task, expectedTaskRevision=1)
                _, accepted, _ = self.rpc("tools/call", dict(name="notifications_test", arguments=test), session)
                self.assertEqual(test["operationId"], accepted["result"]["structuredContent"]["operationId"])
                self.assertTrue(arrived.wait(15))
                self.rpc("tools/call", dict(name="notifications_test", arguments=test), session)
                self.assertEqual(["/mcp-saved"], paths)
            finally:
                sink.shutdown()
                thread.join(timeout=5)

    def test_rejects_protocol(self):
        """actual mcp protocol rejects ambiguous json and requires initialization without durable effects."""
        runtime = self.enable()
        before = self.host.durable_state(runtime)
        status, error, _ = self.rpc("ping", raw=b'{"jsonrpc":"2.0","id":1,"method":"ping","method":"tools/list"}')
        self.assertEqual(200, status)
        self.assertEqual(-32700, error["error"]["code"])
        _, uninitialized, headers = self.rpc("initialize", dict(protocolVersion="2025-11-25",
                                              capabilities={}, clientInfo=dict(name="fixture", version="1")))
        self.assertIn("result", uninitialized)
        session = headers["mcp-session-id"]
        _, error, _ = self.rpc("tools/list", session=session)
        self.assertEqual(-32002, error["error"]["code"])
        _, error, _ = self.rpc("tools/list", session=session, version="invalid")
        self.assertEqual(-32602, error["error"]["code"])
        self.assertEqual(before, self.host.durable_state(runtime))


if __name__ == "__main__":
    unittest.main()
