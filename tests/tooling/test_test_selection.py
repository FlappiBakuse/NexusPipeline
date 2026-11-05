import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from test_selection import select_tests
from test_runner import run_tests
from test_expectations import load_expected_methods


class TestSelectionTests(unittest.TestCase):
    root = Path(__file__).resolve().parents[2]

    def test_python_closure(self):
        result = select_tests(self.root, 'tooling', ['test_test_expectations.TestExpectationTests.test_composes_counts'])
        self.assertEqual([], result['projects'])
        self.assertEqual(1, len(result['methods']))
        self.assertIn('NexusPipeline.CoreTests', select_tests(self.root, 'tooling')['projects'])

    def test_storage_closure(self):
        methods = load_expected_methods(self.root, 'storage')
        data = next(name for name in methods if '.RecordStoreTests.' in name)
        actual = next(name for name in methods if '.BusinessPreservationTests.' in name)
        self.assertEqual(['NexusPipeline.StorageDataTests'], select_tests(self.root, 'storage', [data])['projects'])
        mixed = select_tests(self.root, 'storage', [data, actual])
        self.assertEqual(['NexusPipeline.StorageDataTests', 'NexusPipeline.StorageTests'], mixed['assemblies'])
        self.assertIn('NexusPipeline.Host', mixed['projects'])

    def test_unknown_selection(self):
        with self.assertRaises(ValueError):
            select_tests(self.root, 'tooling', ['unknown.test'])

    def test_method_limit(self):
        with tempfile.TemporaryDirectory(dir=os.environ['NEXUS_TEST_ROOT']) as directory:
            methods = list(load_expected_methods(self.root, 'core'))[:65]
            with patch('test_runner.build_product') as build:
                with self.assertRaises(ValueError):
                    run_tests(self.root, Path(directory) / 'build', Path(directory) / 'reports', 'core', method=methods)
                build.assert_not_called()
