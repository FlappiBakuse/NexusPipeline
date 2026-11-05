import json
import unittest
import uuid
import test_host_boundary as host_boundary


class ControlExecutionTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def test_rejects_forgery(self):
        """strict execution requests reject caller subject and forged native authority before acceptance."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        before = self.host.durable_state(runtime)
        selection = dict(format="nexus.control.execution-selection", version=1,
                         target=dict(kind="binding", bindingId=str(uuid.uuid4())),
                         expectedCatalogRevision=0, expectedSettingsRevision=0)
        for changed in (selection | dict(subjectId="local:forged"),
                        selection | dict(target=dict(kind="pid", processId=123)),
                        selection | dict(expectedCatalogRevision=-1)):
            status, _, _ = self.host.secret_request(address, "/control/v1/execution/preview",
                                                    json.dumps(changed).encode(),
                                                    {"Content-Type": "application/json"})
            self.assertEqual(400, status)
            self.assertEqual(before, self.host.durable_state(runtime))
        status, _, _ = self.host.secret_request(
            address, "/control/v1/execution/" + str(uuid.uuid4()) + "/cancel/" + str(uuid.uuid4()),
            json.dumps(dict(format="nexus.control.execution-cancellation", version=1,
                            runId=str(uuid.uuid4()), attemptId=str(uuid.uuid4()))).encode(),
            {"Content-Type": "application/json"})
        self.assertEqual(409, status)
        self.assertEqual(before, self.host.durable_state(runtime))
