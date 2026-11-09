import hashlib
import json
import struct

from ci_fixture import GitFixture
from desktop_archive import prepare_work, read_archive, source_entries, verify_archive


class ArchiveTests(GitFixture):
    def setUp(self):
        super().setUp()
        self.write('apps/desktop/main.cjs', b'require("electron");\n')
        self.write('apps/desktop/package.json', b'{"main":"main.cjs"}\n')
        self.write('tooling/desktop-tools/package.json', b'{}\n')
        self.write('tooling/desktop-tools/package-lock.json', b'{}\n')
        self.entries = source_entries(self.repo)

    def archive(self, entries=None, *, overrides=None, data=None, raw=None):
        header, payload = {}, b''
        for name, value in (self.entries if entries is None else entries).items():
            digest = hashlib.sha256(value).hexdigest()
            header[name] = dict(size=len(value), offset=str(len(payload)), integrity=dict(
                algorithm='SHA256', hash=digest, blockSize=4194304, blocks=[digest]))
            payload += value
        for name, fields in (overrides or {}).items():
            header[name].update(fields)
        text = json.dumps(dict(files=header)).encode() if raw is None else raw
        padded = text + b'\0' * (-len(text) % 4)
        header_size = 8 + len(padded)
        target = self.scope / 'app.asar'
        target.write_bytes(struct.pack('<IIII', 4, header_size, header_size - 4, len(text)) + padded +
                           (payload if data is None else data))
        return target

    def test_binds_source(self):
        for entries in ({'package.json': self.entries['package.json']}, self.entries | {'extra.cjs': b'extra'}):
            with self.subTest(members=tuple(entries)), self.assertRaisesRegex(ValueError, 'member bytes differ'):
                verify_archive(self.repo, self.archive(entries))
        archive = self.archive()
        verify_archive(self.repo, archive)
        self.write('apps/desktop/main.cjs', b'require("changed");\n')
        with self.assertRaisesRegex(ValueError, 'member bytes differ'):
            verify_archive(self.repo, archive)

    def test_rejects_members(self):
        data = b''.join(self.entries.values())
        unsupported = 'unsupported member'
        unowned = 'unowned or overlapping data'
        cases = (
            ('link', dict(overrides={'main.cjs': {'link': 'package.json'}}), unsupported),
            ('path', dict(entries=self.entries | {'../main.cjs': b'extra'}), unsupported),
            ('duplicate', dict(entries=self.entries | {'MAIN.CJS': b'extra'}), unsupported),
            ('outside', dict(overrides={'main.cjs': {'offset': str(len(data))}}), 'exceeds its data bound'),
            ('overlap', dict(entries={'main.cjs': b'ab', 'package.json': b'bc'},
                             overrides={'package.json': {'offset': '1'}}, data=b'abc'), unowned),
            ('integrity', dict(data=b'x' + data[1:]), 'integrity differs from its bytes'),
            ('unpacked', dict(overrides={'main.cjs': {'unpacked': True}}), unsupported),
            ('duplicate-header', dict(raw=b'{"files":{},"files":{}}'), 'duplicate fields'),
            ('invalid-header', dict(raw=b'[]'), 'source header is invalid'),
            ('truncated', dict(data=data[:-1]), 'exceeds its data bound'),
            ('trailing', dict(data=data + b'extra'), unowned),
        )
        for fault, arguments, error in cases:
            with self.subTest(fault=fault), self.assertRaisesRegex(ValueError, error):
                read_archive(self.archive(**arguments))

    def test_preserves_work(self):
        work = self.scope / 'work'
        prepare_work(self.repo, work, self.entries)
        prepare_work(self.repo, work, self.entries)
        target = work / 'app/main.cjs'
        target.write_bytes(b'changed original bytes')
        with self.assertRaisesRegex(ValueError, 'unknown bytes'):
            prepare_work(self.repo, work, self.entries)
        self.assertEqual(b'changed original bytes', target.read_bytes())
        foreign = self.scope / 'foreign'
        foreign.mkdir()
        sentinel = foreign / 'preserve.txt'
        sentinel.write_bytes(b'original')
        with self.assertRaisesRegex(ValueError, 'ownership'):
            prepare_work(self.repo, foreign, self.entries)
        self.assertEqual(b'original', sentinel.read_bytes())
