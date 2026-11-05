import json
import io
import subprocess
import unittest
import urllib.parse
import urllib.request
import uuid
import zipfile
import test_host_boundary as host_boundary
import test_control_notifications as notification_fixture


class ControlRecordTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def test_exports_diagnostics(self):
        """support export http and cli include only bounded safe diagnostics."""
        runtime = self.host.scope / "runtime-private-path-canary"
        _, identity = self.host.start(runtime)
        address = identity["address"]
        helper = notification_fixture.ControlNotificationTests(methodName="runTest")
        helper.host = self.host
        secret = "https://example.invalid/private-credential-" + uuid.uuid4().hex
        task_name = "private-notification-name-" + uuid.uuid4().hex
        saved = helper.command("save", 0, taskId=str(uuid.uuid4()), expectedTaskRevision=0,
                               input=helper.task(task_name), secrets=[dict(field="webhook-url",
                               expectedRevision=0, disposition="set", value=secret)])
        self.assertEqual(200, helper.call(address, saved)[0])
        before = self.host.durable_state(runtime)
        with urllib.request.urlopen(address + "/control/v1/support", timeout=10) as response:
            self.assertEqual("application/zip", response.headers.get_content_type())
            self.assertIn("no-store", response.headers["Cache-Control"])
            self.assertIn("nexus-support.zip", response.headers["Content-Disposition"])
            exported = response.read(65537)
        cli = subprocess.run([str(self.host.executable), "control", "support", "--address", address,
                              "--binary-stdout", "true"], capture_output=True, timeout=15)
        self.assertEqual(0, cli.returncode, cli.stderr.decode("utf-8", errors="replace"))
        self.assertEqual(b"", cli.stderr)
        for package in (exported, cli.stdout):
            self.assertLessEqual(len(package), 65536)
            with zipfile.ZipFile(io.BytesIO(package)) as archive:
                files = ["doctor.json", "environment.json", "plugins.json", "runtime-state.json", "safe-log.json"]
                self.assertEqual(["manifest.json", *files], archive.namelist())
                expanded = b"".join(archive.read(name) for name in archive.namelist())
                self.assertLess(len(expanded), 65536)
                for private in (secret, task_name, str(runtime), "runtime-private-path-canary"):
                    self.assertNotIn(private.encode(), expanded)
                manifest = json.loads(archive.read("manifest.json"))
                report = json.loads(archive.read("doctor.json"))
                self.assertEqual("nexus.diagnostic.support", manifest["format"])
                self.assertEqual(files, manifest["contents"])
                self.assertEqual("0.18.0", manifest["hostVersion"])
                self.assertEqual("0.1.0", manifest["sdkVersion"])
                self.assertEqual(identity["instanceId"], report["instanceId"])
                self.assertEqual(manifest["reportId"], report["reportId"])
                self.assertEqual(manifest["capturedAtUtc"], report["capturedAtUtc"])
                self.assertEqual({"format", "version", "reportId", "instanceId", "capturedAtUtc",
                                  "admission", "recoveryRevision", "isolatedResourceCount", "heldResourceCount",
                                  "pendingOperationCount", "attemptReceiptCount", "completedRunCount", "details"}, set(report))
                self.assertEqual([], report["details"]["safeLogTail"])
                self.assertEqual("ready", report["details"]["recoveryState"])
                self.assertEqual(0, report["details"]["installedExtensionCount"])
                checks = {item["id"]: item for item in report["details"]["checks"]}
                self.assertEqual({"host.version", "host.admin-integrity", "host.install-write", "web.listener", "mcp.listener",
                                  "recovery.state", "update.transaction", "config.recovery", "execution.state", "scheduler.state",
                                  "plugin.runtime", "plugin.pending", "dependency.python", "dependency.adb", "logs.recent"}, set(checks))
                self.assertEqual("pass", checks["host.install-write"]["status"])
                self.assertEqual("pass", checks["web.listener"]["status"])
                for check in ("mcp.listener", "dependency.python", "dependency.adb", "logs.recent"):
                    self.assertEqual("notRequired", checks[check]["status"])
                self.assertEqual([], json.loads(archive.read("plugins.json")))
                self.assertEqual([], json.loads(archive.read("safe-log.json")))
                self.assertEqual("0.18.0", json.loads(archive.read("environment.json"))["hostVersion"])
        self.assertEqual(400, self.host.request(address, "/control/v1/support?include=secrets")[0])
        self.assertEqual(403, self.host.request(address, "/control/v1/support", "https://example.invalid")[0])
        self.assertEqual(before, self.host.durable_state(runtime))

    def test_reads_status(self):
        """actual status doctor and bounded empty history queries do not mint content or write runtime."""
        runtime = self.host.scope / "runtime"
        _, identity = self.host.start(runtime)
        address = identity["address"]
        baseline = self.host.durable_state(runtime)
        for path, expected in (("/control/v1/status", "nexus.control.status"), ("/control/v1/doctor", "nexus.runtime.diagnostic")):
            status, body = self.host.request(address, path)
            self.assertEqual(200, status)
            data = json.loads(body)
            self.assertEqual(expected, data["format"])
            self.assertEqual(identity["instanceId"], data["instanceId"])
            for private in ("runtimeRoot", "databasePath", "subjectId", "resourceKey", "reasonCode", "secretId"):
                self.assertNotIn(private, data)
        run, attempt = str(uuid.uuid4()), str(uuid.uuid4())
        for path in ("/control/v1/records", "/control/v1/records/" + run + "/attempts"):
            status, body = self.host.request(address, path)
            self.assertEqual(200, status)
            self.assertEqual([], json.loads(body)["items"])
        queries = [
            dict(limit="201"), dict(limit="01"), dict(limit="0"), dict(unknown="1"),
            dict(endedFromInclusive="2026-10-22T00:00:00+08:00"),
            dict(endedFromInclusive="2026-10-22T00:00:00Z", endedUntilExclusive="2026-10-22T00:00:00Z"),
            dict(afterEndedAt="2026-10-22T00:00:00Z"),
            dict(lifecycle="running"), dict(lifecycle="unknown"), dict(lifecycle="Cancelled"),
        ]
        for query in queries:
            with self.subTest(query=query):
                status, _ = self.host.request(address, "/control/v1/records?" + urllib.parse.urlencode(query))
                self.assertEqual(400, status)
        status, body = self.host.request(address, "/control/v1/records?lifecycle=cancelled&business=succeeded")
        self.assertEqual(200, status)
        self.assertEqual([], json.loads(body)["items"])
        for utc in ("2026-10-22T00:00:00Z", "2026-10-22T00:00:00+00:00"):
            status, body = self.host.request(address, "/control/v1/records?" +
                                             urllib.parse.urlencode(dict(endedFromInclusive=utc)))
            self.assertEqual(200, status)
            self.assertEqual([], json.loads(body)["items"])
        for suffix in ("/logs", "/images", "/logs/main/main/description"):
            status, body = self.host.request(address, "/control/v1/records/" + run + "/attempts/" + attempt + suffix)
            self.assertEqual(409, status)
            self.assertEqual("resource_unavailable", json.loads(body)["code"])
        forged = dict(format="nexus.control.record-retirement", version=1, operationId=str(uuid.uuid4()),
                      afterSha256="", limit=1, contents=[dict(relativePath="outside/unknown", sha256="a" * 64)])
        status, _, _ = self.host.secret_request(address, "/control/v1/records/" + run + "/retire/images",
                                               json.dumps(forged).encode(), {"Content-Type": "application/json"})
        self.assertEqual(400, status)
        self.assertEqual(baseline, self.host.durable_state(runtime))


if __name__ == "__main__":
    unittest.main()
