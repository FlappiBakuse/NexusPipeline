import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from local_build_cache import ComponentCache


class ComponentCacheTests(unittest.TestCase):
    def test_reuse_and_input_change(self):
        with tempfile.TemporaryDirectory() as temporary:
            events=[]; cache=ComponentCache(Path(temporary),events); calls=[]
            def build(target):
                calls.append(target);target.mkdir();(target/'result').write_text('compiled')
            first=cache.prepare('host',{'source':'a','sdk':'1'},build)
            self.assertEqual(first,cache.prepare('host',{'source':'a','sdk':'1'},build))
            second=cache.prepare('host',{'source':'b','sdk':'1'},build)
            self.assertNotEqual(first,second)
            self.assertEqual(len(calls),2)
            self.assertEqual([e['cacheHit'] for e in events],[False,True,False])

    def test_changed_or_extra_output_is_rejected(self):
        for mutation in ('bytes','extra','missing'):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                cache=ComponentCache(Path(temporary),[])
                def build(target):
                    target.mkdir();(target/'result').write_text('compiled')
                output=cache.prepare('host','input',build)
                if mutation=='bytes': (output/'result').write_text('tampered')
                elif mutation=='extra': (output/'extra').write_text('unowned')
                else: (output/'result').unlink()
                with self.assertRaises(ValueError): cache.prepare('host','input',build)

    def test_failure_never_becomes_a_hit(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache=ComponentCache(Path(temporary),[])
            def fail(target):
                target.mkdir();(target/'partial').write_text('partial');raise RuntimeError('failed')
            with self.assertRaises(RuntimeError): cache.prepare('host',[],fail)
            with self.assertRaises(FileNotFoundError): cache.prepare('host',[],fail)
            self.assertFalse(list(Path(temporary).rglob('complete.json')))
            self.assertFalse(list(Path(temporary).rglob('*.lock')))

    def test_concurrent_owner_is_not_overwritten(self):
        with tempfile.TemporaryDirectory() as temporary:
            cache=ComponentCache(Path(temporary),[])
            def build(target):
                with self.assertRaises(FileExistsError): cache.prepare('host',[],build)
                target.mkdir();(target/'result').write_text('complete')
            cache.prepare('host',[],build)


if __name__=='__main__': unittest.main()
