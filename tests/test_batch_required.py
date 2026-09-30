import copy
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec=importlib.util.spec_from_file_location("batch_required",Path(__file__).with_name("batch-required.py"))
required=importlib.util.module_from_spec(spec);spec.loader.exec_module(required)


class BatchRequiredTests(unittest.TestCase):
    def setUp(self):
        self.temporary=tempfile.TemporaryDirectory(prefix="batch-required-");self.addCleanup(self.temporary.cleanup)
        self.root=Path(self.temporary.name)
        self.plan_file=self.root/"scope.json";self.report_file=self.root/"batch-report.json"
        unit={"id":"host.docs","kind":"gate","provides":["host.docs"],"expectedCaseIds":[],"expectedMethodIds":[],"expectedScenarioIds":[],"expectedEditorCaseIds":[]}
        self.plan={"schemaVersion":2,"repository":"Host","capacityStatus":"PLANNED","estimatedTotalJobs":5,"control":{"id":"control","units":[unit]},"batches":[],
                   "requiredObligations":["host.docs"],"dirty":False,"prNumber":7,"baseSha":"b"*40,"headSha":"c"*40,"mergeBase":"d"*40,"testedSha":"a"*40,
                   "runId":"12","attempt":"2","partnerSha":None,"sourceFingerprint":"e"*64,"policyDigest":"f"*64}
        self.actual={"id":"host.docs","providedObligations":["host.docs"],"completedCaseIds":[],"completedMethodIds":[],"completedScenarioIds":[],"completedEditorCaseIds":[],
                     "status":"PASS","exitCode":0,"rawEvidence":["unit-receipt.json"],"real":[],"substituted":[]}
        self.report={"schemaVersion":2,"scope":"LOCAL_BATCH_RESULT","qualification":"CI_PRODUCER_PENDING_AUDIT","batchId":"control",
                     "identity":{"repository":"FlappiBakuse/NexusPipeline","prNumber":7,"baseSha":"b"*40,"headSha":"c"*40,"mergeBaseSha":"d"*40,"testedSha":"a"*40,
                                 "runId":"12","attempt":"2","partnerSha":None,"source":{"commitSha":"a"*40,"workingTreeDirty":False},"sourceFingerprint":"e"*64},
                     "policyDigest":"f"*64,"status":"PASS","exitCode":0,"cleanupComplete":True,"units":[self.actual],"artifacts":[],
                     "timing":{"qualificationMs":150000,"hardTimeoutMs":180000,"processElapsedMs":100,"completeJobMs":None}}
        toolchain={"node":"v24.20.0","dotnet":None,"platform":"linux","arch":"x64","rid":"win-x64","buildModes":["production","test-host"]}
        self.report["identity"]["toolchain"]=toolchain
        self.report["identity"]["toolchainFingerprint"]=required.digest(json.dumps(toolchain,separators=(",",":")).encode())
        self.write("unit-receipt.json",{"unitId":"host.docs","provides":["host.docs"],"status":"PASS","exitCode":0})

    def write(self,name,value):
        target=self.root/name;target.parent.mkdir(parents=True,exist_ok=True)
        target.write_text(json.dumps(value),encoding="utf-8")
        self.report["artifacts"]=[item for item in self.report["artifacts"] if item["path"]!=name]
        self.report["artifacts"].append({"path":name,"sha256":required.digest(target.read_bytes()),"sizeBytes":target.stat().st_size})

    def check(self):
        self.plan_file.write_text(json.dumps(self.plan),encoding="utf-8")
        self.report["planDigest"]=required.digest(self.plan_file.read_bytes())
        self.report_file.write_text(json.dumps(self.report),encoding="utf-8")
        return required.validate_bundle(self.plan,self.plan_file,[self.report_file])

    def test_complete_typed_control_passes(self):
        self.assertEqual(self.check()["status"],"PASS")

    def test_finite_union_requires_every_native_scenario_and_observation(self):
        scenarios = ["H-E01", "H-E02", "H-E03"]
        expected = {"id":"host.finite", "kind":"finite", "provides":["execution", "config", "control"],
                    "expectedCaseIds":[], "expectedScenarioIds":scenarios, "expectedEditorCaseIds":[], "expectedMethodIds":[]}
        actual = {"status":"PASS", "exitCode":0, "providedObligations":expected["provides"],
                  "completedScenarioIds":scenarios, "completedCaseIds":[], "completedMethodIds":[], "completedEditorCaseIds":[],
                  "rawEvidence":[f"finite-{scenario}.json" for scenario in scenarios]}
        originals = {scenario:{"scenarioId":scenario, "status":"PASS", "cleanup":"complete"} for scenario in scenarios}
        originals["H-E01"].update(runs=list(range(12)), queries=64)
        originals["H-E02"]["recovery"]={"recovered":True, "idempotentRestart":True}
        files = {name:self.root/name for name in actual["rawEvidence"]}
        for scenario,value in originals.items(): self.write(f"finite-{scenario}.json", value)
        required.validate_unit(expected, actual, files, self.root)
        for scenario in scenarios:
            name=f"finite-{scenario}.json"
            for change in [{"status":"FAIL"}, {"cleanup":"incomplete"}, {"scenarioId":"foreign"}]:
                self.write(name, {**originals[scenario], **change})
                with self.subTest(scenario=scenario, change=change),self.assertRaises(ValueError):
                    required.validate_unit(expected, actual, files, self.root)
            self.write(name, originals[scenario])
            with self.subTest(missing=scenario),self.assertRaises(ValueError):
                required.validate_unit(expected, {**actual, "rawEvidence":[item for item in actual["rawEvidence"] if item!=name]}, files, self.root)
        for scenario,change in [("H-E01", {"queries":63}), ("H-E01", {"runs":list(range(11))}),
                                ("H-E02", {"recovery":{"recovered":True, "idempotentRestart":False}})]:
            self.write(f"finite-{scenario}.json", {**originals[scenario], **change})
            with self.subTest(observation=change),self.assertRaises(ValueError):required.validate_unit(expected, actual, files, self.root)
            self.write(f"finite-{scenario}.json", originals[scenario])

    def test_foreign_identities_dirty_diagnostic_and_cleanup_fail(self):
        original=copy.deepcopy(self.report)
        for key in ["repository","prNumber","baseSha","headSha","mergeBaseSha","testedSha","runId","attempt","partnerSha","sourceFingerprint"]:
            self.report=copy.deepcopy(original);self.report["identity"][key]="foreign"
            with self.subTest(identity=key),self.assertRaises(ValueError):self.check()
        self.report=copy.deepcopy(original);self.report["identity"]["source"]["workingTreeDirty"]=True
        with self.assertRaises(ValueError):self.check()
        self.report=copy.deepcopy(original);self.report["cleanupComplete"]=False
        with self.assertRaises(ValueError):self.check()
        self.report=copy.deepcopy(original);self.plan["diagnosticSelection"]=True
        with self.assertRaises(ValueError):self.check()

    def test_missing_skipped_duplicate_units_and_obligations_fail(self):
        for change in [lambda r:r.update(units=[]),lambda r:r["units"].append(copy.deepcopy(r["units"][0])),
                       lambda r:r["units"][0].update(status="NOT_RUN"),lambda r:r["units"][0].update(providedObligations=[]),
                       lambda r:r["units"][0].update(rawEvidence=[])]:
            previous=copy.deepcopy(self.report);change(self.report)
            with self.assertRaises(ValueError):self.check()
            self.report=previous
        with self.assertRaises(ValueError):required.validate_bundle(self.plan,self.plan_file,[])

    def test_hash_case_collision_absolute_parent_and_oversize_fail(self):
        original=copy.deepcopy(self.report)
        self.report["artifacts"][0]["sha256"]="0"*64
        with self.assertRaises(ValueError):self.check()
        self.report=copy.deepcopy(original);self.report["artifacts"].append({**self.report["artifacts"][0],"path":"UNIT-RECEIPT.JSON"})
        with self.assertRaises(ValueError):self.check()
        for name in ["../outside","/absolute","C:/absolute","a\\b","a//b","a/./b"]:
            with self.subTest(path=name),self.assertRaises(ValueError):required.safe_file(self.root,name)
        self.report=copy.deepcopy(original);self.report["artifacts"][0]["sizeBytes"]=2**30
        with self.assertRaises(ValueError):self.check()

    def test_150000_inclusive_150001_and_server_time_substitution_fail(self):
        self.report["timing"]["processElapsedMs"]=150000;self.check()
        self.report["timing"]["processElapsedMs"]=150001
        with self.assertRaises(ValueError):self.check()
        self.report["timing"]["processElapsedMs"]=1;self.report["timing"]["completeJobMs"]=1
        with self.assertRaises(ValueError):self.check()

    def test_bom_duplicate_json_and_nonfinite_fail(self):
        for content in [b'\xef\xbb\xbf{}',b'{"a":1,"a":2}',b'{"a":NaN}']:
            file=self.root/"bad.json";file.write_bytes(content)
            with self.assertRaises(ValueError):required.load(file)

    def test_reparse_link_is_rejected(self):
        value=mock.Mock(st_mode=0,st_file_attributes=0x400)
        with mock.patch.object(Path,"lstat",return_value=value),self.assertRaises(ValueError):required.unlinked(self.root)

    @unittest.skipUnless(os.name == "nt", "Windows extended-length path boundary")
    def test_long_owned_path_preserves_containment_and_bytes(self):
        relative = "evidence/" + "x" * 90 + "/" + "y" * 100 + ".json"
        target = required.native_path(self.root / relative)
        target.parent.mkdir(parents=True)
        target.write_bytes(b'{"value":1}')
        try:
            self.assertGreater(len(str(target)), 260)
            self.assertEqual(required.load(required.safe_file(self.root, relative)), {"value": 1})
            with self.assertRaises(ValueError): required.safe_file(self.root, "../outside.json")
        finally:
            target.unlink()

    def test_native_trx_instances_skips_and_counter_disagreement_fail(self):
        file=self.root/"native.trx"
        def trx(outcome="Passed",total=2):
            file.write_text(f'<TestRun><Results><UnitTestResult testName="N.Theory(value: 1)" outcome="Passed"/><UnitTestResult testName="N.Theory(value: 2)" outcome="{outcome}"/></Results><Counters total="{total}" executed="2" passed="2" failed="0" notExecuted="0"/></TestRun>',encoding="utf-8")
        trx();self.assertEqual(len(required.native_report("trx",file)["caseIds"]),2)
        for outcome,total in [("NotExecuted",2),("Failed",2),("Passed",1)]:
            trx(outcome,total)
            with self.assertRaises(ValueError):required.native_report("trx",file)

    def test_pending_begin_wait_is_bounded_and_foreign_failure_is_immediate(self):
        clock={"now":0}
        def sleep(seconds):clock["now"]+=seconds
        args=(None,None,"owner/repo",self.plan,{})
        with mock.patch.object(required,"trusted_begin",side_effect=[required.BeginPending("queued"),15368]):
            self.assertEqual(required.wait_for_trusted_begin(*args,deadline=5,clock=lambda:clock["now"],sleeper=sleep),15368)
        self.assertEqual(clock["now"],3)
        clock["now"]=0
        with mock.patch.object(required,"trusted_begin",side_effect=required.BeginPending("queued")),self.assertRaises(ValueError):
            required.wait_for_trusted_begin(*args,deadline=5,clock=lambda:clock["now"],sleeper=sleep)
        self.assertEqual(clock["now"],5)
        clock["now"]=0
        with mock.patch.object(required,"trusted_begin",side_effect=ValueError("foreign")),self.assertRaisesRegex(ValueError,"foreign"):
            required.wait_for_trusted_begin(*args,deadline=5,clock=lambda:clock["now"],sleeper=sleep)
        self.assertEqual(clock["now"],0)

    def test_missing_or_old_trusted_begin_fails(self):
        audit=mock.Mock();audit.api.return_value={"app":{"id":15368}}
        final=mock.Mock();final.current_check.return_value=None
        with self.assertRaises(ValueError):required.trusted_begin(audit,final,"owner/repo",self.plan,{"check_suite_id":1})
        final.current_check.return_value={};final.registration.return_value={"pr":7,"run":12,"attempt":1}
        with self.assertRaises(ValueError):required.trusted_begin(audit,final,"owner/repo",self.plan,{"check_suite_id":1})

    def test_trusted_main_manual_begin_and_foreign_events(self):
        audit=mock.Mock();final=mock.Mock()
        identity={"pr":self.plan["prNumber"],"run":int(self.plan["runId"]),"attempt":int(self.plan["attempt"]),"beginRun":22,"beginAttempt":1,"controllerSha":"b"*40}
        final.current_check.return_value={"id":77};final.registration.return_value=identity
        begin={"id":22,"run_attempt":1,"event":"workflow_dispatch","path":".github/workflows/final-budget.yml","head_branch":"main","head_sha":"b"*40,"repository":{"full_name":"owner/repo"},"status":"completed","conclusion":"success"}
        audit.completed_jobs.return_value=[{}];audit.audit.return_value=[{"status":"PASS"}]
        def check(value):
            audit.api.side_effect=[{"app":{"id":15368}},value]
            return required.trusted_begin(audit,final,"owner/repo",self.plan,{"check_suite_id":1})
        self.assertEqual(check(begin),15368)
        for field,value in [("event","push"),("head_branch","develop"),("head_sha","c"*40),("conclusion","failure")]:
            with self.subTest(field=field),self.assertRaises(ValueError):check({**begin,field:value})

    def test_observation_fixture_identity_survives_case_sorting_and_missing_fails(self):
        expected={"id":"plugins.runtime:Fixture","kind":"plugin","artifact":"Fixture","provides":["adapter"],"expectedCaseIds":["a-case","z-observe"],"observationFixtureId":"z-observe","expectedEditorCaseIds":[],"expectedScenarioIds":[],"expectedMethodIds":[]}
        actual={"status":"PASS","exitCode":0,"providedObligations":["adapter"],"completedCaseIds":["a-case","z-observe"],"completedScenarioIds":[],"completedMethodIds":[],"completedEditorCaseIds":[],"rawEvidence":["a-case.json","z-observe.json"]}
        files={}
        for name in expected["expectedCaseIds"]:
            file=self.root/(name+".json");files[file.name]=file
            self.write(file.name,{"artifact":"Fixture","passed":1,"failed":0,"skipped":0,"completedCaseIds":[name],"observeCount":12 if name=="z-observe" else 1,"isolatedRunChecked":name=="z-observe","editorCaseIds":[]})
        required.validate_unit(expected,actual,files,self.root)
        value=required.load(files["z-observe.json"]);value["observeCount"]=1;self.write("z-observe.json",value)
        with self.assertRaises(ValueError): required.validate_unit(expected,actual,files,self.root)


if __name__=="__main__":unittest.main()
