import importlib.util,json,tempfile,unittest
from pathlib import Path
spec=importlib.util.spec_from_file_location('partner_contract',Path(__file__).with_name('partner-contract.py'))
module=importlib.util.module_from_spec(spec);spec.loader.exec_module(module)

class PartnerInventoryTests(unittest.TestCase):
    def setUp(self):
        self.tmp=tempfile.TemporaryDirectory();self.addCleanup(self.tmp.cleanup)
        self.root=Path(self.tmp.name);self.host=self.root/'host';self.plugins=self.root/'plugins'
        (self.host/'frontend/src/plugin-bridge').mkdir(parents=True);(self.plugins/'tests').mkdir(parents=True)
        self.policy={}
        for index in range(14):
            name='Plugin'+str(index);relative='plugins/specialized/'+name
            folder=self.plugins/relative;folder.mkdir(parents=True)
            (folder/'plugin.json').write_text(json.dumps({'artifactName':name,'kind':'data-specialized'}),encoding='utf-8')
            self.policy[name]={'root':relative,'kind':'data-specialized'}
        self.save()
    def save(self): (self.plugins/'tests/policy.json').write_text(json.dumps({'plugins':self.policy}),encoding='utf-8')
    def test_registered_fourteenth_is_valid(self):self.assertEqual(len(module.check(self.host,self.plugins)['artifacts']),14)
    def test_missing_registration_manifest_duplicate_and_case_collision_fail(self):
        original=self.policy.copy()
        for mode in ['missing-registration','missing-manifest','duplicate','case-collision']:
            with self.subTest(mode=mode):
                self.policy=original.copy();manifest=self.plugins/'plugins/specialized/Plugin13/plugin.json'
                data={'artifactName':'Plugin13','kind':'data-specialized'}
                if mode=='missing-registration':del self.policy['Plugin13']
                if mode=='missing-manifest':manifest.unlink()
                if mode=='duplicate':data['artifactName']='Plugin12'
                if mode=='case-collision':self.policy['plugin13']=self.policy['Plugin13']
                self.save()
                if mode!='missing-manifest':manifest.write_text(json.dumps(data),encoding='utf-8')
                with self.assertRaises(ValueError):module.check(self.host,self.plugins)
                manifest.write_text(json.dumps({'artifactName':'Plugin13','kind':'data-specialized'}),encoding='utf-8')
