import struct
import unittest

from desktop_branding import icon_images


class DesktopBrandingTests(unittest.TestCase):
    def test_preserves_images(self):
        payload = b'product-image'
        entry = struct.pack('<BBBBHHII', 32, 32, 0, 0, 1, 32, len(payload), 22)
        data = struct.pack('<HHH', 0, 1, 1) + entry + payload
        self.assertEqual([(entry[:12], payload)], icon_images(data))

    def test_rejects_offsets(self):
        for data in (b'', b'foreign', struct.pack('<HHH', 0, 1, 65),
                     struct.pack('<HHH', 0, 1, 1) + struct.pack('<BBBBHHII', 32, 32, 0, 0, 1, 32, 8, 999)):
            with self.subTest(data=data), self.assertRaises(ValueError):
                icon_images(data)
