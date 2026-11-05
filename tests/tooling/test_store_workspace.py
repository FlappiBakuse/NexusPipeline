import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'tooling'))
from binary_inputs import hash_file
from source_snapshot import write_json
from store_workspace import prepare_generation, publish_generation, verify_store


class StoreWorkspaceTests(unittest.TestCase):
    def setUp(self):
        self.scope = tempfile.TemporaryDirectory(dir=os.environ['NEXUS_TEST_ROOT'], prefix='store-')
        self.output = Path(self.scope.name) / 'store'

    def tearDown(self):
        self.scope.cleanup()

    def generation(self, sequence):
        generation = str(uuid.uuid4())
        directory, previous = prepare_generation(self.output, generation, sequence)
        payload = directory / 'bundle/package.zip'
        payload.write_bytes(str(sequence).encode())
        record = {'status': 'PASS_LOCAL_SIGNED_STORE', 'pointer': {'sequence': sequence, 'generationId': generation},
                  'files': [{'path': payload.name, 'sizeBytes': payload.stat().st_size, 'sha256': hash_file(payload)}]}
        return directory, previous, record

    def test_retains_generation(self):
        first, previous, record = self.generation(1)
        publish_generation(self.output, first, record, previous)
        original = (self.output / 'store-receipt.json').read_bytes()
        following, previous, next_record = self.generation(2)
        self.assertEqual(original, (self.output / 'store-receipt.json').read_bytes())
        publish_generation(self.output, following, next_record, previous)
        self.assertEqual(original, (first / 'store-receipt.json').read_bytes())
        self.assertEqual(b'1', (first / 'bundle/package.zip').read_bytes())
        self.assertEqual(following / 'bundle', verify_store(self.output, json.loads((self.output / 'store-receipt.json').read_bytes())))

    def test_preserves_changes(self):
        directory, previous, record = self.generation(1)
        publish_generation(self.output, directory, record, previous)
        path = directory / 'bundle/package.zip'
        path.write_bytes(b'user-change')
        with self.assertRaisesRegex(ValueError, 'changed'):
            prepare_generation(self.output, str(uuid.uuid4()), 2)
        self.assertEqual(b'user-change', path.read_bytes())

    def test_rejects_rollback(self):
        directory, previous, record = self.generation(4)
        publish_generation(self.output, directory, record, previous)
        for sequence in (1, 4):
            with self.assertRaisesRegex(ValueError, 'higher sequence'):
                prepare_generation(self.output, str(uuid.uuid4()), sequence)
        extra = directory / 'bundle/unknown.json'
        extra.write_text('preserved', encoding='utf-8')
        with self.assertRaisesRegex(ValueError, 'unowned'):
            prepare_generation(self.output, str(uuid.uuid4()), 5)
        self.assertEqual('preserved', extra.read_text(encoding='utf-8'))

    def test_binds_pointer(self):
        first, previous, record = self.generation(1)
        publish_generation(self.output, first, record, previous)
        directory, previous, following = self.generation(2)
        pointer = self.output / 'store-receipt.json'
        original = pointer.read_bytes()
        pointer.write_bytes(original + b'\n')
        with self.assertRaisesRegex(ValueError, 'pointer changed'):
            publish_generation(self.output, directory, following, previous)
        self.assertEqual(original + b'\n', pointer.read_bytes())
        self.assertEqual(b'1', (first / 'bundle/package.zip').read_bytes())
