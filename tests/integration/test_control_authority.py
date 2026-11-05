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
