import json
import os
from pathlib import Path
import time
import unittest
import urllib.parse
import uuid
import test_host_boundary as host_boundary
import test_control_automation as automation_control


class ControlConfigurationTests(unittest.TestCase):
    def setUp(self):
        self.host = host_boundary.HostBoundaryTests(methodName="runTest")
        self.host.setUp()

    def tearDown(self):
        self.host.tearDown()

    def test_restores_configuration(self):
        """actual editor save and value patch restore live bytes and invalidate the host snapshot."""
        _, started = self.host.start(self.host.scope / "runtime")
        address = started["address"]
        helper = automation_control.ControlAutomationTests(methodName="runTest")
        helper.host = self.host
        _, (_, script_id, _, binding) = helper.seed(address)

        def get(path):
            status, raw = self.host.request(address, path)
            self.assertEqual(200, status, raw)
            return json.loads(raw)

        executable = Path(os.environ["NEXUS_PROCESS_FIXTURE_EXE"])
        editor_root, data_root = str(uuid.uuid4()), str(uuid.uuid4())
        data_directory = self.host.scope / "configuration"
        data_directory.mkdir()
        target = data_directory / "settings.json"
        original = b'{"fixture":"original"}'
        target.write_bytes(original)
        script = next(item for item in get("/control/v1/automation")["bundle"]["scripts"] if item["id"] == script_id)
        script = script | dict(configurationPaths=[dict(rootId=data_root, relative=target.name)])
        change = helper.change(1, scripts=[dict(expectedRevision=1, value=script)], roots=[
            dict(id=editor_root, expectedRevision=0, name="editor", kind="external", path=str(executable.parent)),
            dict(id=data_root, expectedRevision=0, name="configuration", kind="external", path=str(data_directory))])
        status, changed = helper.post(address, "/control/v1/automation/change", change)
        self.assertEqual(200, status, changed)
        prefix = "/control/v1/configuration/generic/" + binding
        resource = get(prefix)["resources"][0]
        self.assertFalse(resource["isSaved"])
        input_path, report = self.host.scope / "editor-input.json", self.host.scope / "editor-ready.json"
        input_path.write_text(json.dumps(dict(operations=[dict(target=str(target), kind="file", value='{"fixture":false}')])) , encoding="utf-8")
        session = str(uuid.uuid4())
        begin = dict(format="nexus.control.native-editor", version=1, sessionId=session, bindingId=binding,
                     expectedBindingRevision=1, expectedScriptRevision=2, editorRootId=editor_root,
                     expectedEditorRootRevision=1, editorRelativePath=executable.name,
                     arguments=["configuration-editor-writer", str(input_path), str(report)],
                     resources=[dict(resourceId=resource["resourceId"], expectedRevision=0)])
        status, opened = helper.post(address, "/control/v1/configuration/editor/begin", begin)
        self.assertEqual(200, status, opened)
        deadline = time.monotonic() + 15
        while not report.exists():
            self.assertLess(time.monotonic(), deadline)
            time.sleep(0.02)
        self.assertEqual(dict(fixture=False), json.loads(target.read_bytes()))
        finish = dict(format="nexus.control.native-editor-finish", version=1, operationId=str(uuid.uuid4()),
                      expectedSessionRevision=opened["revision"])
        status, saved = helper.post(address, "/control/v1/configuration/editor/" + session + "/save", finish)
        self.assertEqual(200, status, saved)
        self.assertEqual(original, target.read_bytes())
        status, replay = helper.post(address, "/control/v1/configuration/editor/begin", begin)
        self.assertEqual(200, status, replay)
        self.assertFalse(replay["isNew"])
        self.assertEqual(saved["session"]["phase"], replay["phase"])
        resource = get(prefix)["resources"][0]
        resource_path = prefix + "/" + resource["resourceId"]
        source = get(resource_path + "/files?revision=" + str(resource["revision"]))["items"][0]
        patch = dict(format="nexus.control.configuration-value", version=1, expectedBindingRevision=1,
                     relativePath=source["relativePath"], syntax="json",
                     patch=dict(format="nexus.config.patch", version=1, operationId=str(uuid.uuid4()),
                                resourceId=resource["resourceId"], sourceSha256=source["sha256"],
                                expectedRevision=resource["revision"], edits=[dict(selector=["fixture"], expected=False, value=True)]))
        snapshot = get("/control/v1/events/snapshot")
        status, applied = helper.post(address, prefix + "/value/apply", patch)
        self.assertEqual(200, status, applied)
        current = get("/control/v1/events/snapshot")
        self.assertEqual(snapshot["streamId"], current["streamId"])
        self.assertGreater(current["sequence"], snapshot["sequence"])
        self.assertGreater(current["state"]["mutationSequence"], snapshot["state"]["mutationSequence"])
        source = get(resource_path + "/files?revision=" + str(applied["revision"]))["items"][0]
        parameters = dict(revision=applied["revision"], sourceSha256=source["sha256"])
        if source["relativePath"]:
            parameters["relativePath"] = source["relativePath"]
        query = urllib.parse.urlencode(parameters)
        self.assertEqual(dict(fixture=True), get(resource_path + "/original?" + query))
        self.assertEqual(original, target.read_bytes())

    def test_rejects_targets(self):
        """generic value entry rejects caller access contract and target authority before any saved write."""
        runtime = self.host.scope / "runtime"
        _, started = self.host.start(runtime)
        address = started["address"]
        binding = str(uuid.uuid4())
        command = dict(format="nexus.control.configuration-value", version=1, expectedBindingRevision=1,
                       relativePath="", syntax="json", patch=dict(format="nexus.config.patch", version=1,
                       operationId=str(uuid.uuid4()), resourceId="generic-" + "a" * 64,
                       sourceSha256="b" * 64, expectedRevision=1,
                       edits=[dict(selector=["fixture"], expected=True, value=False)]))
        before = self.host.durable_state(runtime)
        for extra in (dict(access=dict(write=[])), dict(contract=dict(revision=1)),
                      dict(targetPath="C:\\arbitrary"), dict(syntax="opaque")):
            status, _, _ = self.host.secret_request(address, "/control/v1/configuration/generic/" + binding + "/value/apply",
                                                    json.dumps(command | extra).encode(),
                                                    {"Content-Type": "application/json"})
            self.assertEqual(400, status)
            self.assertEqual(before, self.host.durable_state(runtime))
        status, _ = self.host.request(address, "/control/v1/configuration/generic/" + binding)
        self.assertEqual(404, status)
        self.assertEqual(before, self.host.durable_state(runtime))
