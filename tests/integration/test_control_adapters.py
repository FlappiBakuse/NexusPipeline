import json
import unittest
import uuid
import test_host_boundary as host_boundary


class ControlAdapterTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def test_rejects_authority(self):
        """adapter control cannot mint selection authority from client run contract or access fields."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        selection = dict(operationId=str(uuid.uuid4()), bindingId=str(uuid.uuid4()), expectedBindingRevision=1,
                         extensionId="controlled-adapter-fixture", expectedInstalledRevision=1, resources=[])
        command = dict(format="nexus.control.adapter-command", version=1, action="discover", selection=selection)
        before = self.host.durable_state(runtime)
        for extra in (dict(subjectId="local:forged"), dict(runId=str(uuid.uuid4())),
                      dict(attemptId=str(uuid.uuid4())), dict(planDigest="a" * 64),
                      dict(access=dict(write=[])), dict(contract=dict(revision=1))):
            status, _, _ = self.host.secret_request(address, "/control/v1/adapters/discover",
                json.dumps(command | dict(selection=selection | extra)).encode(), {"Content-Type": "application/json"})
            self.assertEqual(400, status)
        status, _, _ = self.host.secret_request(address, "/control/v1/adapters/discover",
            json.dumps(command | dict(action="propose")).encode(), {"Content-Type": "application/json"})
        self.assertEqual(400, status)
        self.assertEqual(before, self.host.durable_state(runtime))
