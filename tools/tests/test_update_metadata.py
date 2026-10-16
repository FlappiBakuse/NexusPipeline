from __future__ import annotations

import copy
import json
from pathlib import Path
import tempfile
import unittest

from tools.update_metadata import validate_channels


class UpdateMetadataTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="nxp-update-metadata-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.file = self.root / "metadata/updates/channels.json"
        self.file.parent.mkdir(parents=True)
        target = {"releaseTag": "v0.17.3", "manifestAssetName": "NexusPipeline-v0.17.3-win-x64-update.json",
                  "readerProtocol": 1, "requiredCapabilities": ["payload-manifest-v1"]}
        self.bridge = {"sourceReaderProtocol": 1, "sourceCapabilities": ["payload-manifest-v1"],
                       "targetReaderProtocol": 2, "targetCapabilities": ["future-v2"], "entryTarget": target}
        self.data = {"schemaVersion": 1, "bridges": [], "channels": {
            "stable": {"previousPublishedTarget": None, "pendingTarget": None},
            "prerelease": {"previousPublishedTarget": None, "pendingTarget": target}}}

    def validate(self, data):
        self.file.write_text(json.dumps(data), encoding="utf-8")
        validate_channels(self.root, "0.17.3", schema=1)

    def test_bounded_bridge_and_empty_initial_channels_are_valid(self):
        self.validate(self.data)
        self.data["bridges"].append(self.bridge)
        self.validate(self.data)

    def test_ambiguous_unknown_and_unbounded_bridges_are_rejected(self):
        for fault in ("duplicate", "count", "field", "protocol", "capability", "entry"):
            with self.subTest(fault=fault):
                data = copy.deepcopy(self.data)
                bridge = copy.deepcopy(self.bridge)
                data["bridges"] = [bridge]
                if fault == "duplicate": data["bridges"].append(copy.deepcopy(bridge))
                if fault == "count": data["bridges"] = [copy.deepcopy(bridge) for _ in range(9)]
                if fault == "field": bridge["command"] = "ignored"
                if fault == "protocol": bridge["sourceReaderProtocol"] = True
                if fault == "capability": bridge["targetCapabilities"] = ["same", "same"]
                if fault == "entry": bridge["entryTarget"]["manifestAssetName"] = "wrong.json"
                with self.assertRaises(ValueError): self.validate(data)

    def test_duplicate_keys_fail_before_metadata_is_accepted(self):
        self.file.write_text('{"schemaVersion":1,"schemaVersion":1}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            validate_channels(self.root, schema=1)

    def test_v2_requires_exact_assets_capabilities_and_baseline_hint(self):
        path = self.root / 'metadata/updates/v2/channels.json'
        path.parent.mkdir()
        target = {'releaseTag': 'v0.17.4', 'readerProtocol': 2, 'basePayloadManifestSha256': 'a' * 64,
                  'requiredCapabilities': ['payload-manifest-v2', 'single-delta-v1', 'transaction-recovery-v1'],
                  'assets': {key: 'NexusPipeline-v0.17.4-win-x64-' + suffix for key, suffix in
                             (('setup', 'Setup.exe'), ('portable', 'Portable.zip'), ('update', 'Update.zip'))}}
        data = {'schemaVersion': 2, 'channels': {'stable': {'previousPublishedTarget': None, 'pendingTarget': None},
                'prerelease': {'previousPublishedTarget': None, 'pendingTarget': target}}}
        path.write_text(json.dumps(data), encoding='utf-8')
        validate_channels(self.root, '0.17.4')
        for fault in ('asset', 'capabilities', 'baseline', 'schema', 'field'):
            changed = copy.deepcopy(data)
            pending = changed['channels']['prerelease']['pendingTarget']
            if fault == 'asset': pending['assets']['setup'] = 'NexusPipeline-v0.17.4-win-x64-setup.exe'
            if fault == 'capabilities': pending['requiredCapabilities'] = ['payload-manifest-v1']
            if fault == 'baseline': pending['basePayloadManifestSha256'] = '../unknown'
            if fault == 'schema': changed['schemaVersion'] = 1
            if fault == 'field': pending['script'] = 'execute'
            path.write_text(json.dumps(changed), encoding='utf-8')
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                validate_channels(self.root, '0.17.4')
