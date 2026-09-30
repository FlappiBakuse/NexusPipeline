import importlib.util
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
spec=importlib.util.spec_from_file_location("required",Path(__file__).with_name("gate-required.py"))
required=importlib.util.module_from_spec(spec);spec.loader.exec_module(required)
class RequiredEntryTests(unittest.TestCase):
    def test_missing_plan_fails_before_any_network_or_execution(self):
        with tempfile.TemporaryDirectory(prefix="required-entry-") as temporary:
            root=Path(temporary)
            with mock.patch.object(sys,"argv",["gate-required.py",str(root),str(root)]),self.assertRaisesRegex(ValueError,"Missing/duplicate"):
                required.main()
if __name__=="__main__":
    unittest.main()
