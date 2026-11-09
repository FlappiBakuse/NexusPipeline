import io
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from dotnet_bundle import MARKER, verify_publish


class BundleTests(unittest.TestCase):
    def bundle(self, fault=''):
        native = bytearray(512)
        native[:2] = b'MZ'
        struct.pack_into('<I', native, 60, 64)
        native[64:68] = b'PE\0\0'
        struct.pack_into('<HH', native, 68, 0x8664, 1)
        struct.pack_into('<H', native, 84, 112)
        struct.pack_into('<H', native, 88, 0x20b)
        struct.pack_into('<II', native, 216, 256, 256)
        native[400:432] = MARKER
        stream = io.BytesIO(native)
        stream.seek(512)
        entries = []
        def add(name, kind, data):
            entries.append((name, stream.tell(), len(data), kind))
            stream.write(data)
        def string(value):
            raw = value.encode('utf-8')
            self.assertLess(len(raw), 128)
            stream.write(bytes([len(raw)]) + raw)
        add('NexusPipeline.Host.dll', 1, b'MZ-managed')
        add('NexusPipeline.Extensibility.dll', 2 if fault == 'sdk-kind' else 1, b'fixed-sdk')
        add('NexusPipeline.Host.deps.json', 3, b'{}')
        frameworks = [{'name': name, 'version': '10.0.13'} for name in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')]
        config = {'runtimeOptions': {'frameworks': frameworks}}
        if fault == 'self-contained':
            config = {'runtimeOptions': {'includedFrameworks': frameworks}}
        add('NexusPipeline.Host.runtimeconfig.json', 4, json.dumps(config).encode())
        header = stream.tell()
        stream.write(struct.pack('<IIi', 7 if fault == 'version' else 6, 0, len(entries)))
        string('controlled-producer-fixture')
        stream.write(struct.pack('<qqqqQ', entries[2][1], entries[2][2], entries[3][1] + (fault == 'config'), entries[3][2], 0))
        for index, (name, position, size, kind) in enumerate(entries):
            if fault == 'outside' and index == 0:
                position = 0
            if fault == 'overlap' and index == 1:
                position = entries[0][1]
            if fault == 'duplicate' and index == 1:
                name = entries[0][0].upper()
            if fault == 'path' and index == 0:
                name = '../outside.dll'
            stream.write(struct.pack('<qqqB', position, size, int(fault == 'compressed' and index == 0), kind))
            string(name)
        raw = bytearray(stream.getvalue())
        struct.pack_into('<q', raw, 392, header)
        if fault == 'marker':
            raw[400] = 0
        return bytes(raw[:-2] if fault == 'truncated' else raw)

    def inspect_publish(self, fault='', sdk=b'fixed-sdk'):
        with tempfile.TemporaryDirectory(dir=os.environ['NEXUS_TEST_ROOT']) as directory:
            path = Path(directory) / 'NexusPipeline.Host.exe'
            path.write_bytes(self.bundle(fault))
            return verify_publish(path, 'Host', sdk)

    def test_rejects_metadata(self):
        for fault, reason in (('marker', 'bundle marker'), ('version', 'bundle version/count'), ('outside', 'member identity/range'),
                ('overlap', 'overlapping members'), ('duplicate', 'member identity/range'), ('compressed', 'member identity/range'),
                ('path', 'member path'), ('config', 'configuration binding'), ('truncated', 'truncated input')):
            with self.subTest(fault=fault), self.assertRaisesRegex(ValueError, reason):
                self.inspect_publish(fault)

    def test_binds_publish(self):
        result = self.inspect_publish()
        self.assertEqual({'Microsoft.NETCore.App': '10.0.13', 'Microsoft.AspNetCore.App': '10.0.13'},
                         {item['name']: item['version'] for item in result['frameworks']})
        for fault, sdk, reason in (('', b'other-sdk', 'shared SDK producer bytes'),
                ('sdk-kind', b'fixed-sdk', r'required member: NexusPipeline\.Extensibility\.dll'),
                ('self-contained', b'fixed-sdk', 'framework-dependent runtime')):
            with self.subTest(fault=fault, reason=reason), self.assertRaisesRegex(ValueError, reason):
                self.inspect_publish(fault, sdk)
