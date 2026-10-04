"""Read-only validation of current-attempt batch evidence and complete predecessor jobs."""
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import subprocess
import sys
import time


def require(condition, message):
    if not condition:
        raise ValueError(message)


def exact(actual, expected):
    return isinstance(actual, list) and len(actual) == len(set(actual)) and sorted(actual) == sorted(expected)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def native_path(path):
    absolute = Path(os.path.abspath(path))
    return Path("\\\\?\\" + str(absolute)) if os.name == "nt" and not str(absolute).startswith("\\\\?\\") else absolute


def load(path, limit=16*1024*1024):
    data = native_path(path).read_bytes()
    require(len(data) <= limit and not data.startswith(b"\xef\xbb\xbf"), "Oversized or BOM evidence")
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate JSON property")
            result[key] = value
        return result
    return json.loads(data, object_pairs_hook=pairs, parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Nonfinite JSON")))


def unlinked(path):
    path = native_path(path)
    for current in [path, *path.parents]:
        value = current.lstat()
        require(not stat.S_ISLNK(value.st_mode) and not getattr(value, "st_file_attributes", 0) & 0x400, "Linked/reparse evidence path")


def safe_file(root, relative):
    require(isinstance(relative, str) and relative and "\\" not in relative and ":" not in relative
            and not relative.startswith("/") and len(relative.split("/")) <= 16
            and all(part not in {"", ".", ".."} for part in relative.split("/")), "Unsafe evidence path")
    root = native_path(root)
    target = root.joinpath(*PurePosixPath(relative).parts)
    unlinked(target)
    require(target.is_file() and target.resolve().is_relative_to(root.resolve()), "Escaped or missing evidence")
    return target


def module(file, name):
    spec = importlib.util.spec_from_file_location(name, file)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


def native_report(kind, file):
    if kind == "trx":
        import xml.etree.ElementTree as ET
        tree = ET.parse(file).getroot()
        nodes = [node.attrib for node in tree.iter() if node.tag.rsplit("}",1)[-1] == "UnitTestResult"]
        counters = [node.attrib for node in tree.iter() if node.tag.rsplit("}",1)[-1] == "Counters"]
        cases = [node["testName"] for node in nodes]
        require(len(counters) == 1 and all(node.get("outcome") == "Passed" for node in nodes), "Failed/skipped native TRX")
        count = counters[0]
        require(int(count["total"]) == int(count["executed"]) == int(count["passed"]) == len(cases)
                and int(count["failed"]) == int(count.get("notExecuted",0)) == 0, "TRX counter disagreement")
    else:
        data = load(file)
        nodes = [case for suite in data["testResults"] for case in suite["assertionResults"]]
        cases = [node["fullName"] for node in nodes]
        require(data["success"] and all(node["status"] == "passed" for node in nodes)
                and data["numTotalTests"] == data["numPassedTests"] == len(cases)
                and data["numFailedTests"] == data.get("numPendingTests",0) == data.get("numTodoTests",0) == 0, "Failed/skipped native Vitest")
    require(cases and len(cases) == len(set(cases)), "Empty/duplicate native instances")
    return {"caseIds": cases, "passed": len(cases), "failed": 0, "skipped": 0}


def validate_unit(expected, actual, files, base):
    require(actual["status"] == "PASS" and actual["exitCode"] == 0, "Unit did not pass")
    require(exact(actual["providedObligations"], expected["provides"]), "Obligation provider mismatch")
    for key in ["ScenarioIds", "EditorCaseIds", "MethodIds"]:
        require(exact(actual.get("completed"+key,[]), expected.get("expected"+key,[])), "Missing expected "+key)
    raw = actual["rawEvidence"]
    require(raw and exact(raw, raw) and all(name in files for name in raw), "Missing/hashless raw evidence")
    paths = [safe_file(base, name) for name in raw]
    def one(name):
        found = [file for file in paths if file.name == name]
        require(len(found) == 1, "Missing/duplicate native evidence: "+name)
        return found[0]
    if expected["kind"] == "backend" or expected["kind"] == "frontend" and expected["expectedCaseIds"]:
        native = native_report("trx" if expected["kind"] == "backend" else "vitest", one("native.trx" if expected["kind"] == "backend" else "native.json"))
        require(exact(native["caseIds"], expected["expectedCaseIds"]) and exact(actual["completedCaseIds"], native["caseIds"]), "Missing native policy instances")
        require(load(one("native-counts.json")) == native, "Normalized native differs from original")
    elif expected["kind"] == "finite":
        for scenario_id in expected["expectedScenarioIds"]:
            scenario = load(one(f"finite-{scenario_id}.json"))
            require(scenario["scenarioId"] == scenario_id and scenario["status"] == "PASS" and scenario["cleanup"] == "complete", "Missing finite scenario native evidence")
            if scenario_id == "H-E01":
                require(len(scenario["runs"]) == 12 and scenario["queries"] == 64, "Missing finite execution observations")
            elif scenario_id == "H-E02":
                require(scenario["recovery"]["recovered"] and scenario["recovery"]["idempotentRestart"], "Missing configuration recovery")
        require(exact(actual["completedScenarioIds"],expected["expectedScenarioIds"]), "Missing finite scenario IDs")
    elif expected["kind"] == "plugin" and expected.get("expectedMethodIds"):
        discovery = load(one("discovery.json"))
        methods = expected["expectedMethodIds"]
        require(exact(discovery["expectedMethods"], methods), "Discovery methods differ from policy")
        listing = one("discovery.txt").read_text(encoding="utf-8")
        prefixes = {method.rsplit(".",1)[0]+"." for method in methods}
        discovered = [line.strip() for line in listing.splitlines() if any(line.strip().startswith(prefix) for prefix in prefixes)]
        require(exact(discovered, discovery["expectedCaseIds"]) and discovered, "Discovery instance/listing mismatch")
        native = native_report("trx", one("native.trx"))
        require(exact(native["caseIds"], discovered) and exact(actual["completedCaseIds"], discovered)
                and exact(list({case.split("(")[0] for case in discovered}), methods), "Native instances differ from discovery")
        require(load(one("native.json")) == native, "Normalized component differs from original")
        if expected["expectedScenarioIds"]:
            capability = load(one("capability.json"))
            require(capability["scenarioId"] in expected["expectedScenarioIds"] and capability["status"] == "PASS"
                    and capability["cleanup"] == "complete", "Missing real capability/cleanup")
    elif expected["kind"] in {"plugin", "partner-jint"} and expected["expectedCaseIds"]:
        plugins = expected.get("partnerPlugins") or [{"artifact":expected["artifact"],"fixtureIds":expected["expectedCaseIds"],"expectedEditorCaseIds":expected["expectedEditorCaseIds"],"observationFixtureId":expected.get("observationFixtureId")}]
        for plugin in plugins:
            observation = plugin.get("observationFixtureId", plugin["fixtureIds"][0])
            require(observation in plugin["fixtureIds"], "Missing predeclared observation fixture")
            for index, fixture in enumerate(plugin["fixtureIds"]):
                name = f"{plugin['artifact']}-{fixture}.json" if expected["kind"] == "partner-jint" else fixture+".json"
                value = load(one(name))
                require(value["artifact"] == plugin["artifact"] and value["passed"] == 1 and value["failed"] == value["skipped"] == 0
                        and value["completedCaseIds"] == [fixture], "Failed/missing native trajectory")
                if fixture == observation:
                    require(value["observeCount"] == 12 and value["isolatedRunChecked"] is True
                            and exact(value["editorCaseIds"],plugin["expectedEditorCaseIds"]), "Missing editor/new-run/12 observations")
        require(exact(actual["completedCaseIds"], expected["expectedCaseIds"]), "Missing trajectory IDs")
    else:
        require(exact(actual["completedCaseIds"], expected["expectedCaseIds"]), "Unexpected typed assertion cases")
        receipt = load(one("unit-receipt.json"))
        require(receipt["unitId"] == expected["id"] and receipt["status"] == "PASS" and receipt["exitCode"] == 0
                and exact(receipt["provides"],expected["provides"]), "Typed assertion receipt mismatch")
        architecture_name={"plugins.inventory":"architecture.json","host.architecture.backend":"architecture-backend.json",
                           "host.architecture.frontend":"architecture-frontend.json","host.partner-contract":"partner-contract.json"}.get(expected["id"])
        if architecture_name:
            architecture=load(one(architecture_name))
            require(architecture["status"] == "PASS" and architecture["violations"] == [], "Failed raw architecture assertions")
            if expected["id"] in {"plugins.inventory","host.partner-contract"}:
                require(architecture["artifacts"] and exact(architecture["artifacts"],architecture["artifacts"]), "Empty/duplicate raw inventory")
        if expected["expectedScenarioIds"]:
            scenario=load(one("finite-scenario.json"))
            require(scenario["scenarioId"] in expected["expectedScenarioIds"] and scenario["status"] == "PASS"
                    and scenario["cleanup"] == "complete", "Missing finite scenario native evidence")
            if scenario["scenarioId"] == "H-E01":
                require(len(scenario["runs"]) == 12 and scenario["queries"] == 64, "Missing finite execution observations")
            elif scenario["scenarioId"] == "H-E02":
                require(scenario["recovery"]["recovered"] and scenario["recovery"]["idempotentRestart"], "Missing configuration recovery")
        if expected["id"] in {"host.integration.store","host.integration.restart-update"}:
            native=one("store-native.tap" if expected["id"] == "host.integration.store" else "runtime-native.tap").read_text(encoding="utf-8")
            require(not re.search(r"^\s*not ok\b",native,re.M),"Failed native TAP")
            for name in ["fail","cancelled","skipped","todo"]:
                require(re.search(r"^# "+name+r" 0\r?$",native,re.M),"Failed/skipped TAP counters")
            require(re.search(r"^# tests [1-9]\d*\r?$",native,re.M),"Zero TAP tests")
        if expected["id"].startswith("plugins.plugin.package:"):
            built = load(one("package-report.json"))
            package = safe_file(one("package-report.json").parent, "package/"+built["fileName"])
            require(built["status"] == "PASS" and built["artifactName"] == expected["artifact"]
                    and built["kind"] == expected["pluginKind"] and built["sha256"] == digest(package.read_bytes())
                    and built["sizeBytes"] == package.stat().st_size, "Production package identity mismatch")


def validate_bundle(plan, plan_file, report_files, official=True):
    require(plan.get("schemaVersion") == 2 and plan["capacityStatus"] == "PLANNED"
            and len(plan["batches"]) <= 5 and plan["estimatedTotalJobs"] <= 10, "Invalid/capacity exceeded plan")
    require(not official or not plan.get("diagnosticSelection") and not plan["dirty"], "Local/dirty diagnostic cannot qualify CI")
    batches = [item for item in [plan["control"],*plan["batches"]] if item["units"]]
    expected = {item["id"]:item for item in batches}
    reports = {}
    for file in report_files:
        unlinked(file)
        value = load(file)
        require(value.get("schemaVersion") == 2 and value.get("scope") == "LOCAL_BATCH_RESULT", "Wrong batch schema")
        require(value["batchId"] not in reports, "Duplicate batch report")
        reports[value["batchId"]] = (file,value)
    require(set(reports) == set(expected), "Missing/unexpected batch report")
    fingerprints = set()
    provided = []
    for batch_id,batch in expected.items():
        file,report = reports[batch_id]
        require(report["status"] == "PASS" and report["exitCode"] == 0 and report["cleanupComplete"] is True, "Failed batch/cleanup")
        require(not official or report["qualification"] == "CI_PRODUCER_PENDING_AUDIT", "Local report cannot qualify CI")
        identity = report["identity"]
        repo = "FlappiBakuse/NexusPipeline"+("-Plugins" if plan["repository"] == "Plugins" else "")
        for key, expected_value in {"repository":repo,"prNumber":plan["prNumber"],"baseSha":plan["baseSha"],"headSha":plan["headSha"],
                "mergeBaseSha":plan["mergeBase"],"testedSha":plan["testedSha"],"runId":plan["runId"],"attempt":plan["attempt"],"partnerSha":plan["partnerSha"]}.items():
            require(identity.get(key) == expected_value, "Foreign batch identity: "+key)
        require(plan.get("inputMode", "default") == ("paired" if plan.get("inputPair") else "default")
                and identity.get("inputMode", "default") == plan.get("inputMode", "default"), "Foreign input mode")
        require(identity.get("inputPair") == plan.get("inputPair"), "Foreign pair in batch report")
        source = identity["source"]
        require(source and source["commitSha"] == plan["testedSha"] and (not official or source["workingTreeDirty"] is False), "Foreign/dirty source")
        require(identity["sourceFingerprint"] == plan["sourceFingerprint"] and re.fullmatch(r"[0-9a-f]{64}",identity["sourceFingerprint"]), "Source fingerprint mismatch")
        toolchain=identity.get("toolchain")
        if official:
            require(isinstance(toolchain,dict) and str(toolchain.get("node","")).startswith("v24.")
                    and toolchain.get("arch") == "x64" and (batch_id == "control" or toolchain.get("platform") == "win32")
                    and toolchain.get("rid") == "win-x64" and exact(toolchain.get("buildModes"),["production","test-host"]), "Foreign toolchain/build modes")
            require(identity["toolchainFingerprint"] == digest(json.dumps(toolchain,ensure_ascii=False,separators=(",",":")).encode()), "Toolchain fingerprint mismatch")
        fingerprints.add(identity["sourceFingerprint"])
        partner_needed = any(unit["kind"] == "partner-jint" or unit["id"] == "host.partner-contract"
                             or unit.get("pluginKind") == "managed-code" and (unit.get("expectedMethodIds") or unit.get("expectedScenarioIds") or unit["id"].startswith("plugins.plugin.package:"))
                             or unit.get("pluginKind") == "data-specialized" and unit["kind"] == "plugin" for unit in batch["units"])
        if official and partner_needed:
            require(identity["partner"] and identity["partner"]["commitSha"] == plan["partnerSha"]
                    and identity["partner"]["workingTreeDirty"] is False and re.fullmatch(r"[0-9a-f]{64}",identity["partnerFingerprint"] or ""), "Foreign/dirty partner")
        require(report["policyDigest"] == plan["policyDigest"] and report["planDigest"] == digest(plan_file.read_bytes()), "Foreign plan/policy")
        timing = report["timing"]
        require(timing["qualificationMs"] == 150000 and timing["hardTimeoutMs"] == 180000 and timing["completeJobMs"] is None
                and 0 <= timing.get("preparationElapsedMs",0) and 0 <= timing["processElapsedMs"]
                and timing["processElapsedMs"]+timing.get("preparationElapsedMs",0) <= 150000, "Local time is invalid or exceeds 150 seconds")
        inventory = report["artifacts"]
        keys = [item["path"].casefold() for item in inventory]
        require(inventory and len(keys) == len(set(keys)) and len(inventory) <= 4096, "Duplicate/colliding/empty artifact inventory")
        files = {}
        total = 0
        for item in inventory:
            payload = safe_file(file.parent,item["path"])
            size = payload.stat().st_size;total += size
            require(0 <= size == item["sizeBytes"] <= 64*1024*1024 and total <= 256*1024*1024, "Evidence size exceeded")
            require(digest(payload.read_bytes()) == item["sha256"], "Artifact hash mismatch")
            files[item["path"]] = payload
        units = report["units"]
        require(exact([item["id"] for item in units],[item["id"] for item in batch["units"]]), "Missing/duplicate/foreign unit")
        by_id = {item["id"]:item for item in units}
        for unit in batch["units"]:
            actual = by_id[unit["id"]]
            validate_unit(unit,actual,files,file.parent)
            provided.extend(actual["providedObligations"])
    require(len(fingerprints) == 1 and exact(provided,plan["requiredObligations"]), "Duplicate/missing global obligations")
    return {"status":"PASS","qualification":"ACTIONS_PREDECESSORS" if official else "LOCAL_VERIFIED","batchCount":len(batches)}


class BeginPending(ValueError):
    pass


def validate_job_names(audit, plan, jobs, expected_names):
    skipped = [job for job in jobs if job.get("runnerlessSkipped")]
    permitted = set()
    if not plan["control"]["units"]:
        permitted.add(audit.job_name(plan["repository"], "control"))
    if not plan["batches"]:
        permitted.add(audit.job_name(plan["repository"], "unselected"))
    require(all(job["name"] in permitted for job in skipped), "Unexpected skipped physical position")
    physical = [job for job in jobs if not job.get("runnerlessSkipped")]
    require(exact([job["name"] for job in physical], expected_names) and len(physical)+2 <= 10, "Unexpected physical producer graph")


def trusted_begin(audit, final, repository, plan, producer):
    suite = audit.api(f"/repos/{repository}/check-suites/{producer['check_suite_id']}")
    check = final.current_check(repository,plan["headSha"],audit.job_name(plan["repository"], "finalBudget"),suite["app"]["id"],plan["prNumber"])
    if check is None: raise BeginPending("Missing trusted begin registration")
    registration = final.registration(check)
    pair = final.ci_inputs.resolve(repository, plan["prNumber"], head=plan["headSha"], base=plan["baseSha"], tested=plan["testedSha"], read=audit.api)
    require(pair == plan.get("inputPair"), "Scope pair differs from current PR pair")
    binding = final.ci_inputs.binding(pair, repository, int(plan["runId"]), int(plan["attempt"]), registration["controllerSha"]) if pair else None
    require(registration["pairDigest"] == (pair["pairDigest"] if pair else None) and registration["bindingDigest"] == binding, "Pair lacks matching trusted begin")
    require(registration["pr"] == plan["prNumber"], "Foreign begin PR")
    if (registration["run"],registration["attempt"]) != (int(plan["runId"]),int(plan["attempt"])):
        raise BeginPending("Old begin registration")
    begin = audit.api(f"/repos/{repository}/actions/runs/{registration['beginRun']}/attempts/{registration['beginAttempt']}")
    require(begin.get("id") == registration["beginRun"] and begin.get("run_attempt") == registration["beginAttempt"]
            and begin.get("event") in ["workflow_run", "workflow_dispatch"] and begin.get("path") == ".github/workflows/final-budget.yml"
            and begin.get("head_branch") == "main"
            and begin.get("head_sha") == registration["controllerSha"] and begin.get("repository",{}).get("full_name") == repository, "Foreign begin controller")
    if begin.get("status") in ["queued", "in_progress", "waiting", "pending"]: raise BeginPending("Begin controller not completed")
    require(begin.get("status") == "completed" and begin.get("conclusion") == "success", "Failed begin controller")
    jobs = audit.completed_jobs(repository,registration["beginRun"],registration["beginAttempt"])
    require(len(jobs) == 1 and all(item["status"] == "PASS" for item in audit.audit(jobs,registration["beginRun"],registration["beginAttempt"])), "Begin complete job budget failed")
    return suite["app"]["id"]


def wait_for_trusted_begin(audit, final, repository, plan, producer, *, deadline, clock=time.monotonic, sleeper=time.sleep):
    while clock() < deadline:
        try:
            return trusted_begin(audit, final, repository, plan, producer)
        except BeginPending as error:
            remaining = deadline-clock()
            if remaining <= 0: break
            print(str(error)+"; waiting for trusted main", flush=True)
            sleeper(min(3,remaining))
    raise ValueError("Trusted begin did not complete within Required work budget")


def main():
    root,reports = map(lambda value:Path(value).resolve(),sys.argv[1:3])
    for directory,children,files in os.walk(reports,followlinks=False):
        unlinked(Path(directory))
        for name in [*children,*files]:
            unlinked(Path(directory)/name)
    plans = list(reports.rglob("scope.json"));require(len(plans) == 1,"Missing/duplicate scope plan")
    plan = load(plans[0]);require(plan.get("schemaVersion") == 2,"Unsupported scope schema")
    repository = os.environ["GITHUB_REPOSITORY"]
    for key,value in {"runId":os.environ["GITHUB_RUN_ID"],"attempt":os.environ["GITHUB_RUN_ATTEMPT"],"testedSha":os.environ["GITHUB_SHA"],
                     "baseSha":os.environ["PR_BASE_SHA"],"headSha":os.environ["PR_HEAD_SHA"],"prNumber":int(os.environ["PR_NUMBER"])}.items():
        require(plan[key] == value,"Foreign scope identity: "+key)
    partner = os.environ.get("NEXUS_PARTNER_ROOT")
    subprocess.run(["node",str(root/"tests/verify-plan.mjs"),str(plans[0]),*([partner] if partner else [])],cwd=root,check=True,stdout=subprocess.DEVNULL,timeout=20)
    result = validate_bundle(plan,plans[0],list(reports.rglob("batch-report.json")))
    needs = json.loads(os.environ["CI_NEEDS"])
    require(needs.get("scope",{}).get("result") == "success" and needs.get("control",{}).get("result") == ("success" if plan["control"]["units"] else "skipped")
            and needs.get("batches",{}).get("result") == ("success" if plan["batches"] else "skipped"), "Selected predecessors failed/skipped")
    audit = module(root/"tests/audit-jobs.py","audit_required")
    final = module(root/"tests/final-budget.py","final_required")
    producer = audit.api(f"/repos/{repository}/actions/runs/{plan['runId']}/attempts/{plan['attempt']}")
    require(producer.get("id") == int(plan["runId"]) and producer.get("run_attempt") == int(plan["attempt"])
            and producer.get("head_sha") == plan["headSha"] and producer.get("event") == "pull_request"
            and producer.get("path") == ".github/workflows/ci.yml" and producer.get("repository",{}).get("full_name") == repository,"Foreign producer")
    started = float(os.environ["NEXUS_TEST_JOB_STARTED_AT_MS"])/1000
    elapsed = time.time()-started
    require(0 <= elapsed < 130, "Required work budget exhausted/invalid start")
    deadline = time.monotonic()+min(100,130-elapsed)
    app_id = wait_for_trusted_begin(audit,final,repository,plan,producer,deadline=deadline)
    jobs,_ = audit.physical_jobs(repository,producer,audit.completed_jobs(repository,int(plan["runId"]),int(plan["attempt"])),audit.job_name(plan["repository"], "finalBudget"),app_id)
    expected_names = json.loads(subprocess.check_output(["node", str(root/"tests/ci-names.mjs"), str(plans[0])], cwd=root, timeout=8))
    names = expected_names[:-1]
    validate_job_names(audit, plan, jobs, expected_names)
    before = [job for job in jobs if job["name"] in names]
    durations = audit.audit(before,int(plan["runId"]),int(plan["attempt"]))
    require(all(item["status"] == "PASS" for item in durations),"Complete predecessor job failed/budget exceeded")
    result.update({"runId":plan["runId"],"attempt":plan["attempt"],"actualJobBudgets":durations,"controllerPostQualification":"PENDING_FINAL_READ_ONLY_ACCEPTANCE"})
    print(json.dumps(result,ensure_ascii=False))


if __name__ == "__main__":
    main()
