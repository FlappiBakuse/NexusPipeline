from __future__ import annotations

import unittest

from tools.pe_manifest import PeManifestError, requested_execution_level


class PeManifestTests(unittest.TestCase):
    def test_requested_execution_level_is_read_from_embedded_manifest_shape(self) -> None:
        manifest = b'''<assembly xmlns="urn:schemas-microsoft-com:asm.v1">
          <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
            <security><requestedPrivileges><requestedExecutionLevel level="asInvoker" /></requestedPrivileges></security>
          </trustInfo>
        </assembly>'''
        self.assertEqual(requested_execution_level(manifest), "asInvoker")

    def test_missing_or_ambiguous_level_is_rejected(self) -> None:
        with self.assertRaises(PeManifestError):
            requested_execution_level(b"<assembly />")
        with self.assertRaises(PeManifestError):
            requested_execution_level(b'''<assembly><requestedExecutionLevel level="asInvoker" /><requestedExecutionLevel level="requireAdministrator" /></assembly>''')


if __name__ == "__main__":
    unittest.main()
