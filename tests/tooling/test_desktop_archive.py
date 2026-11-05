import copy
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
        self.header, self.data = {}, b''
        for name, data in self.entries.items():
            digest = hashlib.sha256(data).hexdigest()
            self.header[name] = dict(size=len(data), offset=str(len(self.data)), integrity=dict(
                algorithm='SHA256', hash=digest, blockSize=4194304, blocks=[digest]))
            self.data += data

    def archive(self, header=None, data=None, raw=None):
        text = json.dumps(dict(files=self.header if header is None else header)).encode() if raw is None else raw
        padded = text + b'\0' * (-len(text) % 4)
        header_size = 8 + len(padded)
        target = self.scope / 'app.asar'
        target.write_bytes(struct.pack('<IIII', 4, header_size, header_size - 4, len(text)) + padded +
                           (self.data if data is None else data))
        return target

    def test_binds_source(self):
        archive = self.archive()
        self.assertEqual(self.entries, read_archive(archive))
        verify_archive(self.repo, archive)
        self.write('apps/desktop/main.cjs', b'require("changed");\n')
        with self.assertRaisesRegex(ValueError, 'source'):
            verify_archive(self.repo, archive)

    def test_rejects_members(self):
        for fault in ('missing', 'extra', 'link', 'path', 'duplicate', 'outside', 'overlap', 'integrity', 'unpacked'):
            header = copy.deepcopy(self.header)
            entry = header['main.cjs']
            if fault == 'missing':
                del header['main.cjs']
            elif fault == 'extra':
                header['extra.cjs'] = copy.deepcopy(entry)
            elif fault == 'link':
                header['main.cjs'] = dict(link='package.json')
            elif fault == 'path':
                header['../main.cjs'] = header.pop('main.cjs')
            elif fault == 'duplicate':
                header['MAIN.CJS'] = copy.deepcopy(entry)
            elif fault == 'outside':
                entry['offset'] = str(len(self.data))
            elif fault == 'overlap':
                header['package.json']['offset'] = '1'
            elif fault == 'integrity':
                entry['integrity']['hash'] = '0' * 64
            else:
                entry['unpacked'] = True
            with self.subTest(fault=fault), self.assertRaises(ValueError):
                verify_archive(self.repo, self.archive(header))
        for raw in (b'{"files":{},"files":{}}', b'[]'):
            with self.subTest(raw=raw), self.assertRaises(ValueError):
                read_archive(self.archive(raw=raw))
        for data in (self.data[:-1], self.data + b'extra'):
            with self.subTest(data=data), self.assertRaises(ValueError):
                read_archive(self.archive(data=data))

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
