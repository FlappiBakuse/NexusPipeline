import copy
import json
import time
import unittest
import uuid
import test_control_automation as automation
import test_host_boundary as boundary


class QueueGroupTests(unittest.TestCase):
    def setUp(self):
        self.host = boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()
        self.control = automation.ControlAutomationTests(methodName="runTest")
        self.control.host = self.host

    def tearDown(self):
        self.host.tearDown()

    def get(self, address, path):
        status, raw = self.host.request(address, path)
        self.assertEqual(200, status, raw)
        return json.loads(raw)

    def save(self, address, command):
        status, result = self.control.post(address, "/control/v1/automation/change", command)
        self.assertEqual(200, status, result)
        return result["catalogRevision"]

    @staticmethod
    def queue(identity, scripts, order, dependencies=None):
        return dict(id=identity, name="queue-" + identity[:8], scriptIds=scripts, order=order,
                    completionAction="none", notifyEnabled=False, notificationTaskIds=[], successDependencies=dependencies or [])

    def preview(self, address, queue, revision):
        settings = self.get(address, "/control/v1/settings")["saved"]
        request = dict(format="nexus.control.execution-selection", version=1, target=dict(kind="queue", queueId=queue),
                       expectedCatalogRevision=revision, expectedSettingsRevision=settings["revision"])
        status, result = self.control.post(address, "/control/v1/execution/preview", request)
        self.assertEqual(200, status, result)
        return request, result

    def test_queue_groups(self):
        runtime = self.host.scope / "runtime"
        process, opened = self.host.start(runtime)
        address = opened["address"]
        seed, (_, script, user, binding) = self.control.seed(address)
        first, second = str(uuid.uuid4()), str(uuid.uuid4())
        self.save(address, self.control.change(1, queues=[
            dict(expectedRevision=0, value=self.queue(first, [script], 1)),
            dict(expectedRevision=0, value=self.queue(second, [], 0))]))
        _, original = self.preview(address, first, 2)
        self.assertEqual([binding], [item["bindingId"] for item in original["items"]])
        newcomer, added = str(uuid.uuid4()), str(uuid.uuid4())
        self.save(address, self.control.change(2,
            users=[dict(expectedRevision=1, value=dict(id=user, name="original", order=1)),
                   dict(expectedRevision=0, value=dict(id=newcomer, name="new", order=0))],
            bindings=[dict(expectedRevision=0, value=dict(id=added, userId=newcomer, scriptId=script, enabled=True,
                                                         priority=0, notifyEnabled=False, notificationTaskIds=[]))],
            queues=[dict(expectedRevision=1, value=self.queue(first, [script], 0)),
                    dict(expectedRevision=1, value=self.queue(second, [], 1))]))
        _, expanded = self.preview(address, first, 3)
        self.assertEqual([added, binding], [item["bindingId"] for item in expanded["items"]])
        self.assertEqual([binding], [item["bindingId"] for item in original["items"]])
        self.save(address, self.control.change(3, bindings=[dict(expectedRevision=1, value=dict(
            id=added, userId=newcomer, scriptId=script, enabled=False, priority=0, notifyEnabled=False, notificationTaskIds=[]))]))
        process.terminate(); process.wait(timeout=15)
        process, opened = self.host.start(runtime); address = opened["address"]
        saved = self.get(address, "/control/v1/automation")["bundle"]
        self.assertEqual([first, second], [queue["id"] for queue in saved["queues"]])
        _, current = self.preview(address, first, 4)
        self.assertEqual([binding], [item["bindingId"] for item in current["items"]])

        unavailable, dependent = str(uuid.uuid4()), str(uuid.uuid4())
        first_binding, dependent_binding = str(uuid.uuid4()), str(uuid.uuid4())
        unavailable_script = copy.deepcopy(seed["scripts"][0]["value"])
        unavailable_script.update(id=unavailable, name="unavailable-group")
        dependent_script = copy.deepcopy(seed["scripts"][0]["value"])
        dependent_script.update(id=dependent, name="dependent-group")
        self.save(address, self.control.change(4,
            scripts=[dict(expectedRevision=0, value=value) for value in (unavailable_script, dependent_script)],
            bindings=[dict(expectedRevision=0, value=dict(id=identity, userId=user, scriptId=group, enabled=True,
                priority=0, runDays=0 if group == unavailable else -1, notifyEnabled=False, notificationTaskIds=[])) for identity, group in
                ((first_binding, unavailable), (dependent_binding, dependent))],
            queues=[dict(expectedRevision=2, value=self.queue(first, [unavailable, dependent, script], 0,
                [dict(scriptId=dependent, requiredScriptIds=[unavailable])]))]))
        request, preview = self.preview(address, first, 5)
        self.assertTrue(preview["hasRunnable"]); self.assertFalse(preview["hasBlockedItems"])
        items = {item["scriptId"]: item for item in preview["items"]}
        self.assertEqual("execution_run_days_exhausted", items[unavailable]["reasonCode"])
        self.assertEqual("execution_queue_dependency_not_successful", items[dependent]["reasonCode"])
        self.assertEqual("runnable", items[script]["disposition"])
        status, accepted = self.control.post(address, "/control/v1/execution/start/" + str(uuid.uuid4()), request)
        self.assertEqual(202, status, accepted)
        deadline = time.monotonic() + 30
        while True:
            records = self.get(address, "/control/v1/records?limit=20")["items"]
            if any(item["finalAttempt"]["runId"] == accepted["runId"] for item in records):
                break
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.05)


if __name__ == "__main__":
    unittest.main()
