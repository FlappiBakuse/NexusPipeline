import os
from pathlib import Path
import subprocess
import tempfile
import unittest

from build_source import source_entries, snapshot


class SourceCaseTests(unittest.TestCase):
    def test_unstaged_case_rename_preserves_actual_path_bytes_and_index(self):
        with tempfile.TemporaryDirectory(dir=os.environ['NEXUS_TEST_ARTIFACT_ROOT']) as temporary:
            root = Path(temporary) / 'source'
            root.mkdir()
            def git(*args):
                return subprocess.check_output(['git', '-C', str(root), *args])
            git('init', '--quiet')
            (root / 'STATUS.md').write_bytes(b'# Status\n')
            git('add', 'STATUS.md')
            before = git('ls-files', '--stage', '-z')
            (root / 'STATUS.md').rename(root / 'temporary.md')
            (root / 'temporary.md').rename(root / 'status.md')
            (root / 'status.md').write_bytes(b'# Current\r\n')
            entries = source_entries(root)
            self.assertEqual(entries, [('status.md', '100644', b'# Current\r\n')])
            snapshot(root, Path(temporary) / 'copy', entries)
            self.assertEqual([p.name for p in (Path(temporary) / 'copy').iterdir()], ['status.md'])
            self.assertEqual(before, git('ls-files', '--stage', '-z'))


if __name__ == '__main__':
    unittest.main()
