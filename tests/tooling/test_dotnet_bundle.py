import io
import json
from pathlib import Path
import struct
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from dotnet_bundle import MARKER, read_bundle, read_member, verify_publish


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
        add('NexusPipeline.Extensibility.dll', 1, b'fixed-sdk')
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

    def test_reads_members(self):
        stream = io.BytesIO(self.bundle())
        files = read_bundle(stream)
        self.assertEqual(4, len(files))
        self.assertEqual(b'fixed-sdk', read_member(stream, files, 'NexusPipeline.Extensibility.dll', 1))
        with self.assertRaises(ValueError):
            read_member(stream, files, 'NexusPipeline.Extensibility.dll', 4)

    def test_rejects_metadata(self):
        for fault in ('marker', 'version', 'outside', 'overlap', 'duplicate', 'compressed', 'path', 'config', 'truncated'):
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                read_bundle(io.BytesIO(self.bundle(fault)))

    def test_binds_publish(self):
        class Published:
            def __init__(self, raw):
                self.raw = raw
            def open(self, mode):
                return io.BytesIO(self.raw)
        result = verify_publish(Published(self.bundle()), 'Host', b'fixed-sdk')
        self.assertEqual(4, result['memberCount'])
        for raw, sdk in ((self.bundle(), b'other-sdk'), (self.bundle('self-contained'), b'fixed-sdk')):
            with self.assertRaises(ValueError):
                verify_publish(Published(raw), 'Host', sdk)
