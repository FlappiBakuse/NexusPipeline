import os
from pathlib import Path
import struct
import tempfile
import unittest

from desktop_branding import brand_executable


class DesktopBrandingTests(unittest.TestCase):
    def test_rejects_offsets(self):
        header = struct.pack('<HHH', 0, 1, 1)
        outside = struct.pack('<BBBBHHII', 32, 32, 0, 0, 1, 32, 8, 999)
        with tempfile.TemporaryDirectory(dir=os.environ['NEXUS_TEST_ROOT']) as directory:
            work = Path(directory) / 'branding'
            for data, error in ((b'foreign', 'Invalid product icon'),
                                (header, 'icon exceeds its bounds'),
                                (header + outside, 'Invalid product icon image')):
                with self.subTest(error=error), self.assertRaisesRegex(ValueError, error):
                    brand_executable(b'unused executable', data, work)
            self.assertFalse(work.exists())
