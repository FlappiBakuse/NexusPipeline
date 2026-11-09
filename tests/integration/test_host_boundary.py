import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
import urllib.parse
import uuid
import concurrent.futures
import http.client
import sqlite3
import contextlib
import time
from host_boundary_state import database_snapshot


class HostBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.root = Path(os.environ["NEXUS_TEST_ROOT"]).resolve(strict=True)
        self.scope = Path(tempfile.mkdtemp(prefix="host-boundary-", dir=self.root)).resolve()
        self.executable = Path(os.environ["NEXUS_TEST_HOST_EXE"]).resolve(strict=True)
        self.binary_hash = hashlib.sha256(self.executable.read_bytes()).hexdigest()
        self.processes = []
        self.ready_processes = []

    def tearDown(self):
        for process in reversed(self.processes):
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=15)
            if process.stdout:
                process.stdout.close()
            if process.stderr:
                process.stderr.close()
        self.assertEqual(self.binary_hash, hashlib.sha256(self.executable.read_bytes()).hexdigest())

    def spawn(self, runtime):
        process = subprocess.Popen(
            [str(self.executable), "serve", "--runtime-root", str(runtime), "--port", "0"],
            stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding="utf-8",
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        self.processes.append(process)
        return process

    def start(self, runtime):
        process = self.spawn(runtime)
        output = queue.Queue()
        threading.Thread(target=lambda: output.put(process.stdout.readline()), daemon=True).start()
        line = output.get(timeout=30)
        self.assertTrue(line, "Host exited before reporting readiness")
        identity = json.loads(line)
        self.assertEqual(process.pid, identity["processId"])
        self.assertEqual("host.ready", identity["kind"])
        self.ready_processes.append((runtime.resolve(), process))
        return process, identity

    @staticmethod
    def native_path(path):
        resolved = path.resolve()
        owner = Path(os.environ["NEXUS_TEST_ROOT"]).resolve(strict=True)
        if owner not in resolved.parents:
            raise ValueError("Fixture path is outside the owned test root")
        return Path("\\\\?\\" + str(resolved))

    def files(self, root):
        result = {}
        active = any(path == root.resolve() and process.poll() is None for path, process in self.ready_processes)
        if active:
            result["state/nexus.db"] = {"committedDataSha256": database_snapshot(root)}
        native = self.native_path(root)
        for path in native.rglob("*"):
            if not path.is_file():
                continue
            relative = path.relative_to(native).as_posix()
            if active and relative in ("state/nexus.db", "state/nexus.db-shm", "state/nexus.db-wal"):
                continue
            if path.name == "instance.lock":
                stat = path.stat()
                result[relative] = {"sizeBytes": stat.st_size, "fileId": stat.st_ino}
            else:
                result[relative] = hashlib.sha256(path.read_bytes()).hexdigest()
        return result

    @staticmethod
    def request(address, path, origin=None, method="GET", headers=None):
        headers = dict(headers or {})
        if origin:
            headers["Origin"] = origin
        request = urllib.request.Request(address + path, headers=headers, method=method)
        try:
            with urllib.request.urlopen(request, timeout=10) as response:
                return response.status, response.read()
        except urllib.error.HTTPError as error:
            with error:
                return error.code, error.read()

    def test_rejects_routes(self):
        """only new control route is available and old routes cannot mutate runtime."""
        runtime = self.scope / "runtime"
        process, identity = self.start(runtime)
        baseline = self.files(runtime)
        status, body = self.request(identity["address"], "/control/v1/capabilities")
        self.assertEqual(200, status)
        capability = json.loads(body)
        self.assertEqual(identity["instanceId"], capability["instanceId"])
        self.assertEqual("0.18.0", capability["hostVersion"])
        self.assertEqual("0.1.0", capability["sdkVersion"])
        self.assertCountEqual(["control.capabilities.read", "control.status.read", "control.doctor.read", "operations.read", "automation.read", "automation.write", "automation.declare", "automation.delete", "automation.exchange", "configuration.read", "configuration.edit", "configuration.repair", "configuration.native.open", "execution.preview", "execution.start", "execution.cancel", "scheduling.read", "records.read", "records.logs.read", "records.images.read", "records.retire", "settings.read", "settings.write", "secrets.status", "secrets.read", "secrets.write", "notifications.read", "notifications.write", "notifications.delete", "notifications.test", "extensions.read", "extensions.write", "completion.local", "maintenance.read", "maintenance.write", "media.preview.grant"], capability["capabilities"])
        for route in ("/api/status", "/api/scripts", "/api/config", "/mcp"):
            with self.subTest(route=route):
                self.assertEqual(404, self.request(identity["address"], route)[0])
        self.assertEqual(baseline, self.files(runtime))

    def test_rejects_origin(self):
        """untrusted origin and unsupported write do not modify the instance."""
        runtime = self.scope / "runtime"
        process, identity = self.start(runtime)
        before = self.files(runtime)
        self.assertEqual(403, self.request(identity["address"], "/control/v1/capabilities", "https://example.invalid")[0])
        self.assertEqual(405, self.request(identity["address"], "/control/v1/capabilities", method="POST")[0])
        self.assertEqual(200, self.request(identity["address"], "/control/v1/capabilities", identity["address"])[0])
        self.assertEqual(before, self.files(runtime))
        deadline = time.monotonic() + 10
        while True:
            with contextlib.closing(sqlite3.connect((runtime / "state/nexus.db").as_uri() + "?mode=ro", uri=True)) as database:
                if database.execute("SELECT count(*) FROM schedule_minute_claims;").fetchone()[0]:
                    break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.01)
        process.terminate()
        process.wait(timeout=15)
        verified = database_snapshot(runtime)
        with sqlite3.connect(runtime / "state/nexus.db") as database:
            created = database.execute("SELECT created_at FROM schema_info;").fetchone()[0]
            database.execute("UPDATE schema_info SET created_at=?;", (created + "-changed",))
        self.assertNotEqual(verified, database_snapshot(runtime))
        with sqlite3.connect(runtime / "state/nexus.db") as database:
            database.execute("UPDATE schema_info SET created_at=?;", (created,))
            database.execute("UPDATE operations SET request_digest=? WHERE operation_kind='schedule.minute';", ("a" * 64,))
        with self.assertRaises(ValueError):
            database_snapshot(runtime)

    def test_preserves_unknown(self):
        """old identity and nonempty unknown root are rejected without writes."""
        for name, filename in (("old", "instance.json"), ("unknown", "config.json")):
            with self.subTest(root=name):
                runtime = self.scope / name
                runtime.mkdir()
                (runtime / filename).write_bytes(b'{"schemaVersion":17,"keep":"original"}')
                before = self.files(runtime)
                process = self.spawn(runtime)
                process.communicate(timeout=30)
                self.assertEqual(1, process.returncode)
                self.assertEqual(before, self.files(runtime))

    def test_rejects_spoofing(self):
        """matching forged host origin and forwarded headers cannot define the listener."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        before = self.files(runtime)
        actual = urllib.parse.urlparse(identity["address"])
        other_port = 1 if actual.port != 1 else 2
        for authority in (f"attacker.invalid:{actual.port}", f"127.0.0.1:{other_port}"):
            with self.subTest(authority=authority):
                headers = {"Host": authority, "X-Forwarded-Host": actual.netloc,
                           "X-Forwarded-Proto": "http", "Forwarded": f'host="{actual.netloc}";proto=http'}
                status, _ = self.request(identity["address"], "/control/v1/capabilities", "http://" + authority, headers=headers)
                self.assertEqual(403, status)
        self.assertEqual(before, self.files(runtime))

    def test_accepts_localhost(self):
        """actual localhost authority is same origin and control responses are not cached."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        before = self.files(runtime)
        port = urllib.parse.urlparse(identity["address"]).port
        authority = f"localhost:{port}"
        request = urllib.request.Request(identity["address"] + "/control/v1/capabilities",
                                         headers={"Host": authority, "Origin": "http://" + authority})
        with urllib.request.urlopen(request, timeout=10) as response:
            self.assertEqual(200, response.status)
            self.assertEqual("no-store", response.headers["Cache-Control"])
            self.assertEqual(identity["instanceId"], json.load(response)["instanceId"])
        self.assertEqual(403, self.request(identity["address"], "/control/v1/capabilities", identity["address"], headers={"Host": authority})[0])
        self.assertEqual(before, self.files(runtime))

    def test_leases_instance(self):
        """instance lease blocks a second host and identity survives restart."""
        runtime = self.scope / "runtime"
        first, identity = self.start(runtime)
        before = self.files(runtime)
        second = self.spawn(runtime)
        second.communicate(timeout=30)
        self.assertEqual(1, second.returncode)
        self.assertEqual(before, self.files(runtime))
        first.terminate()
        first.wait(timeout=15)
        _, restarted = self.start(runtime)
        self.assertEqual(identity["instanceId"], restarted["instanceId"])

    def test_rejects_missing(self):
        """missing initialized database is not rebuilt during host startup."""
        runtime = self.scope / "runtime"
        first, _ = self.start(runtime)
        first.terminate()
        first.wait(timeout=15)
        database = runtime / "state/nexus.db"
        self.assertTrue((runtime / "state-initialization.guard").is_file())
        database.unlink()
        before = self.files(runtime)
        rejected = self.spawn(runtime)
        rejected.communicate(timeout=30)
        self.assertEqual(1, rejected.returncode)
        self.assertFalse(database.exists())
        self.assertEqual(before, self.files(runtime))

    def test_preserves_database(self):
        """unknown database bytes are preserved and only an isolated diagnostic copy is written."""
        runtime = self.scope / "runtime"
        first, _ = self.start(runtime)
        first.terminate()
        first.wait(timeout=15)
        (runtime / "state/nexus.db").write_bytes(b"Unknown storage generation; preserve exact bytes\x00\xff")
        before = self.files(runtime)
        rejected = self.spawn(runtime)
        rejected.communicate(timeout=30)
        self.assertEqual(1, rejected.returncode)
        after = {path: value for path, value in self.files(runtime).items() if not path.startswith("diagnostics/")}
        self.assertEqual(before, after)
        copies = list(self.native_path(runtime / "diagnostics/state-inspection").glob("*/nexus.db"))
        self.assertEqual(1, len(copies))
        self.assertEqual((runtime / "state/nexus.db").read_bytes(), copies[0].read_bytes())

    @staticmethod
    def secret_request(address, route, body=None, headers=None, method="POST"):
        request = urllib.request.Request(address + route, data=body, headers=headers or {}, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=15)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.read(), dict(response.headers)

    @staticmethod
    def mutation(operation=None, revision=0, disposition="set"):
        return {"Nexus-Operation-Id": operation or str(uuid.uuid4()), "Nexus-Expected-Revision": str(revision),
                "Nexus-Secret-Disposition": disposition, "Content-Type": "text/plain; charset=utf-8"}

    @staticmethod
    def secret_route():
        return "/control/v1/secrets/fixture-extension/" + str(uuid.uuid4()) + "/password"

    def assert_secret_absent(self, runtime, material):
        """assert no secret material."""
        native = self.native_path(runtime)
        for path in native.rglob("*"):
            if path.is_file():
                data = path.read_bytes()
                for encoding in ("utf-8", "utf-16-le"):
                    self.assertNotIn(material.encode(encoding), data, str(path.relative_to(native)))

    def durable_state(self, runtime):
        return self.files(runtime)

    def test_preserves_secrets(self):
        """secret round trip replay after clear and restart never recreates plaintext."""
        runtime = self.scope / "runtime"
        first, identity = self.start(runtime)
        address, route = identity["address"], self.secret_route()
        material = "fixture-only-口令-😀- do not trim \n"
        headers = self.mutation()
        status, original, response_headers = self.secret_request(address, route + "/change", material.encode(), headers)
        self.assertEqual(200, status)
        self.assertEqual("no-store", response_headers["Cache-Control"])
        saved = json.loads(original)
        self.assertEqual(1, saved["revision"])
        self.assertTrue(saved["isReadable"])
        self.assertNotIn(material.encode(), original)
        self.assertEqual(original, self.secret_request(address, route + "/change", material.encode(), headers)[1])
        status, plaintext, response_headers = self.secret_request(address, route + "/read", b"")
        self.assertEqual((200, material.encode()), (status, plaintext))
        self.assertEqual("no-store", response_headers["Cache-Control"])
        self.assertEqual("nosniff", response_headers["X-Content-Type-Options"])
        self.assertEqual("text/plain; charset=utf-8", response_headers["Content-Type"])
        keep = self.mutation(revision=1, disposition="keep")
        self.assertEqual(saved, json.loads(self.secret_request(address, route + "/change", b"", keep)[1]))
        clear = self.mutation(revision=1, disposition="clear")
        status, body, _ = self.secret_request(address, route + "/change", b"", clear)
        self.assertEqual(200, status)
        self.assertFalse(json.loads(body)["isConfigured"])
        self.assertEqual(original, self.secret_request(address, route + "/change", material.encode(), headers)[1])
        self.assertEqual("secret_unavailable", json.loads(self.secret_request(address, route + "/read", b"")[1])["code"])
        first.terminate()
        first.wait(timeout=15)
        self.assert_secret_absent(runtime, material)
        _, restarted = self.start(runtime)
        self.assertEqual(identity["instanceId"], restarted["instanceId"])
        status, current, _ = self.secret_request(restarted["address"], route, method="GET")
        self.assertEqual(200, status)
        self.assertFalse(json.loads(current)["isConfigured"])
        self.assertEqual(2, json.loads(current)["revision"])

    def test_rejects_encoding(self):
        """secret invalid encoding limits headers and disposition do not change runtime."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        address, route = identity["address"], self.secret_route()
        before = self.files(runtime)
        cases = [(b"", self.mutation(), 400), (b"\xff\xfe", self.mutation(), 400),
                 (b"x" * 196609, self.mutation(), 413), (b"a" * 65537, self.mutation(), 400),
                 (b"x", self.mutation(disposition="clear"), 413), (b"x", self.mutation(disposition=""), 400)]
        for replacement, value in (("Content-Type", "application/json"), ("Nexus-Operation-Id", "0" * 36),
                                   ("Nexus-Expected-Revision", "01"), ("Nexus-Expected-Revision", "-1")):
            headers = self.mutation()
            headers[replacement] = value
            cases.append((b"fixture", headers, 415 if replacement == "Content-Type" else 400))
        for body, headers, expected in cases:
            with self.subTest(status=expected, size=len(body), disposition=headers["Nexus-Secret-Disposition"]):
                status, error, response_headers = self.secret_request(address, route + "/change", body, headers)
                self.assertEqual(expected, status)
                self.assertEqual("nexus.control.error", json.loads(error)["format"])
                self.assertEqual("no-store", response_headers["Cache-Control"])
        self.assertEqual(400, self.secret_request(address, route + "/change?unexpected=1", b"fixture", self.mutation())[0])
        self.assertEqual(400, self.secret_request(address, route.replace("password", "INVALID"), method="GET")[0])
        self.assertEqual(before, self.files(runtime))

    def test_rejects_forgery(self):
        """secret cross origin and forged authority cannot read or mutate."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        address, route = identity["address"], self.secret_route()
        before = self.files(runtime)
        for origin in ("null", "https://example.invalid", address + "," + address):
            headers = self.mutation()
            headers["Origin"] = origin
            for path, body in (("/change", b"fixture-only"), ("/read", b"")):
                status, error, response_headers = self.secret_request(address, route + path, body, headers)
                self.assertEqual(403, status)
                self.assertEqual("access_denied", json.loads(error)["code"])
                self.assertEqual("no-store", response_headers["Cache-Control"])
        self.assertEqual(405, self.secret_request(address, route + "/read", method="GET")[0])
        self.assertEqual(413, self.secret_request(address, route + "/read", b"not empty")[0])
        self.assertEqual(before, self.files(runtime))

    def test_rejects_corruption(self):
        """secret cipher corruption is unavailable without implicit clear or rewrite."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        address, route = identity["address"], self.secret_route()
        status, body, _ = self.secret_request(address, route + "/change", b"fixture-cipher-corruption", self.mutation())
        self.assertEqual(200, status)
        saved = json.loads(body)
        payloads = list((runtime / "secrets/payloads").glob(saved["secretId"] + "-*.bin"))
        self.assertEqual(1, len(payloads))
        self.native_path(payloads[0]).write_bytes(b"unknown-cipher-bytes-preserve")
        before = self.durable_state(runtime)
        status, body, _ = self.secret_request(address, route, method="GET")
        current = json.loads(body)
        self.assertEqual(200, status)
        self.assertTrue(current["isConfigured"])
        self.assertFalse(current["isReadable"])
        self.assertEqual(saved["secretId"], current["secretId"])
        status, body, response_headers = self.secret_request(address, route + "/read", b"")
        self.assertEqual(409, status)
        self.assertEqual("secret_unavailable", json.loads(body)["code"])
        self.assertEqual("no-store", response_headers["Cache-Control"])
        self.assertEqual(before, self.durable_state(runtime))

    def test_preserves_result(self):
        """secret concurrent same operation and other field conflict preserve original result."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        address, route = identity["address"], self.secret_route()
        headers, material = self.mutation(), b"fixture-concurrent-value"
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            results = list(executor.map(lambda _: self.secret_request(address, route + "/change", material, headers), range(2)))
        self.assertEqual([200, 200], [item[0] for item in results])
        self.assertEqual(results[0][1], results[1][1])
        for target, value in ((route, b"different-fixture"), (self.secret_route(), material)):
            status, error, _ = self.secret_request(address, target + "/change", value, headers)
            self.assertEqual(409, status)
            self.assertEqual("operation_conflict", json.loads(error)["code"])
            self.assertNotIn("secretId", json.loads(error))
        self.assertEqual(1, len(list((runtime / "secrets/payloads").glob("*.bin"))))

    def test_rejects_duplicates(self):
        """secret duplicate operation header is rejected before acceptance."""
        runtime = self.scope / "runtime"
        _, identity = self.start(runtime)
        route = self.secret_route()
        before = self.files(runtime)
        address = urllib.parse.urlparse(identity["address"])
        connection = http.client.HTTPConnection(address.hostname, address.port, timeout=10)
        try:
            connection.putrequest("POST", route + "/change")
            for name, value in self.mutation().items():
                connection.putheader(name, value)
            connection.putheader("Nexus-Operation-Id", str(uuid.uuid4()))
            connection.putheader("Content-Length", "7")
            connection.endheaders(b"fixture")
            response = connection.getresponse()
            self.assertEqual(400, response.status)
            self.assertEqual("secret_request_invalid", json.loads(response.read())["code"])
        finally:
            connection.close()
        self.assertEqual(before, self.files(runtime))


if __name__ == "__main__":
    unittest.main()
