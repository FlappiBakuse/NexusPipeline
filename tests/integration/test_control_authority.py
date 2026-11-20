"""Use the existing actual Host fixture; no alternate HTTP implementation."""
import concurrent.futures
import ctypes
import json
import time
import unittest
import urllib.error
import urllib.request
import uuid

import test_host_boundary as host_fixture


class ControlAuthorityTests(unittest.TestCase):
    def setUp(self):
        self.fixture = host_fixture.HostBoundaryTests(methodName="runTest")
        self.fixture.setUp()

    def tearDown(self):
        self.fixture.tearDown()

    def test_settings_token_changes(self):
        """Local same-origin settings commit the token immediately through its dedicated endpoint."""
        _, identity = self.fixture.start(self.fixture.scope / "runtime")
        address = identity["address"]
        route = "/control/v1/local/control-token/change-from-settings"
        secret = "/control/v1/local/secrets/control-bearer"
        token = b"Ab_9"

        def change(revision, value, origin, site="same-origin", operation=None):
            operation = operation or str(uuid.uuid4())
            headers = {"Nexus-Operation-Id": operation, "Nexus-Expected-Revision": str(revision),
                       "Nexus-Secret-Disposition": "set" if value is not None else "clear",
                       "Sec-Fetch-Site": site}
            if value is not None:
                headers["Content-Type"] = "text/plain; charset=utf-8"
            if origin is not None:
                headers["Origin"] = origin
            request = urllib.request.Request(address + route, data=value, headers=headers, method="POST")
            try:
                with urllib.request.urlopen(request, timeout=5) as response:
                    return response.status, json.load(response)
            except urllib.error.HTTPError as error:
                with error:
                    return error.code, None

        self.assertEqual(403, change(0, token, None)[0])
        self.assertEqual(403, change(0, token, address, "cross-site")[0])
        self.assertEqual(403, change(0, token, "https://example.invalid")[0])
        self.assertEqual(400, change(0, b"too", address)[0])
        self.assertEqual(400, change(0, b"a.b-", address)[0])
        self.assertEqual(0, json.loads(self.fixture.request(address, secret)[1])["revision"])

        operation = str(uuid.uuid4())
        status, saved = change(0, token, address, operation=operation)
        self.assertEqual(200, status)
        self.assertEqual({"operationId": operation, "revision": 1, "isConfigured": True, "isReadable": True}, saved)
        self.assertEqual((200, saved), change(0, token, address, operation=operation))
        self.assertEqual(409, change(0, None, address)[0])
        self.assertTrue(self.fixture.request(address, secret + "/read", method="POST") == (200, token))
        status, result = self.fixture.request(address, "/control/v1/local/control-token/operations/" + operation)
        self.assertEqual(200, status)
        self.assertTrue(json.loads(result)["isDurable"])
        status, rotated = change(1, b"A-_9" * 64, address)
        self.assertEqual(200, status)
        self.assertEqual(2, rotated["revision"])
        self.assertIn(change(2, b"A" * 257, address)[0], (400, 413))
        status, cleared = change(2, None, address)
        self.assertEqual(200, status)
        self.assertEqual(3, cleared["revision"])
        self.assertFalse(cleared["isConfigured"])

    def test_cancels_confirmation(self):
        """a real server confirmation can be cancelled without registering the control credential."""
        runtime = self.fixture.scope / "runtime"
        process, identity = self.fixture.start(runtime)
        baseline = self.fixture.files(runtime)
        operation = str(uuid.uuid4())
        address = identity["address"]
        headers = {"Content-Type": "text/plain; charset=utf-8", "Nexus-Operation-Id": operation,
                   "Nexus-Expected-Revision": "0", "Nexus-Secret-Disposition": "set"}

        def change():
            request = urllib.request.Request(address + "/control/v1/local/control-token/change",
                data=b"controlled_Local_Native_Token_Value_032", headers=headers, method="POST")
            try:
                with urllib.request.urlopen(request, timeout=20) as response:
                    return response.status, response.read()
            except urllib.error.HTTPError as response:
                return response.code, response.read()

        with concurrent.futures.ThreadPoolExecutor(max_workers=1) as pool:
            pending = pool.submit(change)
            deadline = time.monotonic() + 10
            while not self.confirmation_windows(process.pid):
                self.assertFalse(pending.done(), "Actual confirmation ended before its owned Windows dialog was observed")
                self.assertIsNone(process.poll())
                self.assertLess(time.monotonic(), deadline)
                time.sleep(0.02)
            status, body = self.fixture.request(address,
                "/control/v1/local/control-token/operations/" + operation)
            self.assertEqual(200, status)
            self.assertFalse(json.loads(body)["isDurable"])
            status, body = self.fixture.request(address,
                "/control/v1/local/control-token/" + operation + "/cancel", method="POST")
            self.assertEqual(200, status); self.assertTrue(json.loads(body)["cancelled"])
            status, body = pending.result(timeout=15)
            self.assertEqual(403, status)
            self.assertEqual("access_denied", json.loads(body)["code"])
        self.assertEqual(404, self.fixture.request(address,
            "/control/v1/local/control-token/operations/" + operation)[0])
        self.assertEqual(baseline, self.fixture.files(runtime))

    @staticmethod
    def confirmation_windows(process_id):
        user = ctypes.WinDLL("user32", use_last_error=True)
        found = []
        callback_type = ctypes.WINFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)
        user.GetWindowThreadProcessId.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_ulong)]
        user.GetWindowTextW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p, ctypes.c_int]
        user.EnumWindows.argtypes = [callback_type, ctypes.c_void_p]

        @callback_type
        def visit(window, _):
            owner = ctypes.c_ulong()
            user.GetWindowThreadProcessId(window, ctypes.byref(owner))
            if owner.value == process_id:
                title = ctypes.create_unicode_buffer(256)
                user.GetWindowTextW(window, title, len(title))
                if title.value.startswith("NexusPipeline control credential "):
                    found.append(window)
            return True

        if not user.EnumWindows(visit, None):
            raise ctypes.WinError(ctypes.get_last_error())
        return found
