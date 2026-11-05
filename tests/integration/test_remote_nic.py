import http.client
import json
import os
import secrets
import subprocess
import time
import unittest
import urllib.parse
import uuid
import test_host_boundary as host_boundary


class RemoteNicTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def seed(self, runtime, token):
        fixture = os.environ["NEXUS_PROCESS_FIXTURE_EXE"]
        seeded = subprocess.run([fixture, "control-bearer-seed", str(runtime)], input=token.encode("ascii"),
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60,
                                creationflags=subprocess.CREATE_NO_WINDOW)
        self.assertEqual(0, seeded.returncode, seeded.stderr)
        result = json.loads(seeded.stdout)
        self.assertTrue(result["addresses"], "This declared NIC test requires an actual up non-loopback IPv4 interface")
        self.assertNotIn(token.encode(), seeded.stdout + seeded.stderr)
        return result

    @staticmethod
    def remote(address, path, token=None, headers=None, method="GET", body=None):
        uri = urllib.parse.urlsplit(address)
        connection = http.client.HTTPConnection(uri.hostname, uri.port, timeout=15)
        try:
            actual = dict(headers or {})
            if token is not None:
                actual["Authorization"] = "Bearer " + token
            connection.request(method, path, body=body, headers=actual)
            response = connection.getresponse()
            return response.status, response.read(2097153), dict(response.getheaders())
        finally:
            connection.close()

    def save_service(self, address, saved, enabled):
        command = dict(format="nexus.control.service-settings", version=1, operationId=str(uuid.uuid4()),
                       expectedRevision=saved["revision"], service=saved["service"] | dict(allowRemoteAccess=enabled))
        status, raw, _ = self.host.secret_request(address, "/control/v1/settings/service",
                                                  json.dumps(command).encode(), {"Content-Type": "application/json"})
        self.assertEqual(200, status)
        return json.loads(raw)["saved"]

    def open_stream(self, address, token):
        status, raw, _ = self.remote(address, "/control/v1/events/snapshot", token)
        self.assertEqual(200, status)
        snapshot = json.loads(raw)
        uri = urllib.parse.urlsplit(address)
        connection = http.client.HTTPConnection(uri.hostname, uri.port, timeout=3)
        query = urllib.parse.urlencode(dict(streamId=snapshot["streamId"], afterSequence=snapshot["sequence"]))
        connection.request("GET", "/control/v1/events?" + query, headers={"Authorization": "Bearer " + token})
        response = connection.getresponse()
        self.assertEqual(200, response.status)
        self.assertTrue(response.readline())
        return connection, response

    def assert_stream_revoked(self, response):
        deadline = time.monotonic() + 4
        try:
            while time.monotonic() < deadline:
                if not response.readline():
                    return
        except (http.client.IncompleteRead, http.client.RemoteDisconnected, ConnectionResetError):
            return
        self.fail("Existing remote SSE remained authorized after saved access was disabled")

    def test_revokes_remote(self):
        """actual nic bearer authority rotation forwarded spoof and saved remote revocation."""
        runtime = self.host.scope / "runtime"
        token = secrets.token_hex(32)
        seeded = self.seed(runtime, token)
        process, first = self.host.start(runtime)
        status, raw = self.host.request(first["address"], "/control/v1/settings/service")
        self.assertEqual(200, status)
        self.save_service(first["address"], json.loads(raw), True)
        process.terminate()
        process.wait(timeout=15)
        process, opened = self.host.start(runtime)
        port = urllib.parse.urlsplit(opened["address"]).port
        address = "http://" + seeded["addresses"][0] + ":" + str(port)
        path = "/control/v1/capabilities"
        self.assertEqual(401, self.remote(address, path)[0])
        self.assertEqual(401, self.remote(address, path, "0" * 64)[0])
        status, raw, headers = self.remote(address, path, token)
        self.assertEqual(200, status)
        capabilities = json.loads(raw)["capabilities"]
        self.assertIn("execution.start", capabilities)
        for allowed in ("settings.write", "extensions.write", "media.preview.grant"):
            self.assertIn(allowed, capabilities)
        for blocked in ("secrets.read", "configuration.native.open", "desktop.local", "maintenance.write"):
            self.assertNotIn(blocked, capabilities)
        self.assertEqual("no-store", headers.get("Cache-Control"))
        status, raw, _ = self.remote(address, path, token,
                                    {"X-Forwarded-For": "127.0.0.1", "Forwarded": "for=127.0.0.1;host=localhost"})
        self.assertEqual(200, status)
        self.assertNotIn("secrets.read", json.loads(raw)["capabilities"])
        self.assertEqual(403, self.remote(address, path, token, {"Origin": "http://untrusted.invalid"})[0])
        dns = "nexus.example.invalid:" + str(port)
        self.assertEqual(200, self.remote(address, path, token, {"Host": dns, "Origin": "http://" + dns})[0])
        self.assertEqual(401, self.remote(address, path, headers={"Host": dns, "Origin": "http://" + dns})[0])
        self.assertEqual(403, self.remote(address, path, token, {"Host": dns, "Origin": "http://other.example.invalid:" + str(port)})[0])
        self.assertEqual(403, self.remote(address, path, token, {"Host": "nexus.example.invalid:" + str(port + 1)})[0])
        self.assertEqual(403, self.remote(address, path, token, {"Host": "127.0.0.1:" + str(port)})[0])
        status, raw, _ = self.remote(address, "/control/v1/settings/service", token)
        self.assertEqual(200, status)
        saved = json.loads(raw)
        command = dict(format="nexus.control.service-settings", version=1, operationId=str(uuid.uuid4()),
                       expectedRevision=saved["revision"], service=saved["service"])
        status, raw, _ = self.remote(address, "/control/v1/settings/service", token,
                                    {"Content-Type": "application/json"}, "POST", json.dumps(command).encode())
        self.assertEqual(200, status)
        self.assertEqual(saved["service"], json.loads(raw)["saved"]["service"])
        for route in ("/control/v1/extensions/apply", "/control/v1/media/previews"):
            status, raw, _ = self.remote(address, route, token, {"Content-Type": "application/json"}, "POST", b"{}")
            self.assertEqual(400, status, raw)
        status, _ = self.host.request(opened["address"], "/control/v1/local/control-token/operations/" + seeded["operationId"])
        self.assertEqual(200, status)
        self.assertEqual(403, self.remote(address, "/control/v1/local/control-token/operations/" + seeded["operationId"], token)[0])
        status, raw = self.host.request(opened["address"], "/control/v1/settings/service")
        self.assertEqual(200, status)
        stream, response = self.open_stream(address, token)
        try:
            self.save_service(opened["address"], json.loads(raw), False)
            self.assert_stream_revoked(response)
        finally:
            response.close()
            stream.close()
        self.assertEqual(403, self.remote(address, path, token)[0])
        status, raw = self.host.request(opened["address"], "/control/v1/settings/service")
        self.save_service(opened["address"], json.loads(raw), True)
        process.terminate()
        process.wait(timeout=15)
        replacement = secrets.token_hex(32)
        self.seed(runtime, replacement)
        process, rotated = self.host.start(runtime)
        address = "http://" + seeded["addresses"][0] + ":" + str(urllib.parse.urlsplit(rotated["address"]).port)
        self.assertEqual(401, self.remote(address, path, token)[0])
        self.assertEqual(200, self.remote(address, path, replacement)[0])
        process.terminate()
        process.wait(timeout=15)
        self.host.assert_secret_absent(runtime, token)
        self.host.assert_secret_absent(runtime, replacement)


if __name__ == "__main__":
    unittest.main()
