from copy import deepcopy
import json
from unittest.mock import patch
import subprocess

from ci_fixture import GitFixture, HOST, production_tools


class CiPlanTests(GitFixture):
    def test_pins_partner(self):
        self.write("sdk/a.cs", b"public class Exported {}\n")
        head = self.commit()
        with production_tools(HOST) as owner:
            real_git = owner.ci_plan.git

            def fixed(repository, *arguments):
                if arguments[0] == "ls-remote":
                    return (head + "\trefs/heads/develop\n").encode()
                return real_git(repository, *arguments)

            with patch.object(owner.ci_plan, "git", side_effect=fixed):
                plan = owner.ci_plan.make_plan(self.repo, "host", self.base_commit, head, head)
                owner.ci_plan.verify_plan(self.repo, plan)
            self.assertEqual(head, plan["partner"]["commit"])
            self.assertFalse(plan["publishable"])
            for field, value in (("repository", "other/repository"), ("ref", "refs/heads/main"), ("commit", "short")):
                altered = deepcopy(plan)
                altered["partner"][field] = value
                with self.subTest(field=field), self.assertRaises(ValueError):
                    owner.ci_plan.verify_plan(self.repo, altered)

    def test_accepts_merge(self):
        self.write("apps/web/a.ts", b"export {};\n")
        head = self.commit()
        self.git("checkout", "-b", "base-update", self.base_commit)
        self.write("docs/base.md", b"base-only\n")
        base = self.commit()
        self.git("checkout", "develop")
        self.git("-c", "user.name=Release Fixture", "-c", "user.email=fixture@example.invalid",
                 "merge", "--no-ff", "--no-edit", "base-update")
        merged = self.git("rev-parse", "HEAD").decode().strip()
        with production_tools(HOST) as owner:
            plan = owner.ci_plan.make_plan(self.repo, "host", base, head, merged)
            owner.ci_plan.verify_plan(self.repo, plan)
            self.assertEqual(["apps/web/a.ts"], plan["changedPaths"])
            self.assertEqual(merged, plan["source"]["sourceCommit"])

    def test_rejects_outputs(self):
        with production_tools(HOST) as owner:
            for output in (self.repo, self.repo / "output", self.scope):
                with self.subTest(output=str(output)), self.assertRaises(ValueError):
                    owner.ci_build.external(self.repo, None, output)
            occupied = self.scope / "occupied"
            occupied.mkdir()
            sentinel = occupied / "data.txt"
            sentinel.write_bytes(b"preserve")
            with self.assertRaises(ValueError):
                owner.ci_build.external(self.repo, None, occupied)
            self.assertEqual(b"preserve", sentinel.read_bytes())
            target = self.scope / "target"
            target.mkdir()
            link = self.scope / "linked"
            subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(target)], check=True, capture_output=True)
            try:
                with self.assertRaises(ValueError):
                    owner.ci_build.external(self.repo, None, link / "output")
                self.assertEqual([], list(target.iterdir()))
            finally:
                link.rmdir()
    def declarations(self):
        for name, kind, frontend in (("live-screenshot", "managed", True), ("game-checkin", "managed", True),
                                     ("emulator-support", "managed", False), ("maa-framework", "managed", True),
                                     ("maaend", "specialized", False), ("maas", "specialized", False)):
            self.write(f"plugins/{kind}/{name}/extension.json", json.dumps({
                "extensionId": name, "frontend": {} if frontend else None,
                "specialized": {} if name in {"maa-framework", "maaend", "maas"} else None}).encode())

    def test_selects_host(self):
        cases = [("docs/developer/a.md", [], False, False, []),
                 ("tests/tooling/a.py", [], False, False, []),
                 ("apps/web/src/main.ts", [], True, False, []),
                 ("apps/desktop/main.cjs", [], False, True, []),
                 ("apps/installer/Program.cs", ["NexusPipeline.Installer"], False, False, []),
                 ("apps/launcher/Program.cs", ["NexusPipeline.Launcher"], False, False, []),
                 ("apps/uninstaller/Program.cs", ["NexusPipeline.Uninstaller"], False, False, []),
                 ("src/core/a.cs", ["NexusPipeline.Host", "NexusPipeline.Installer", "NexusPipeline.Launcher", "NexusPipeline.Maintenance", "NexusPipeline.Uninstaller"], True, False, []),
                 ("sdk/frontend/a.ts", ["NexusPipeline.Host", "NexusPipeline.Installer", "NexusPipeline.Launcher", "NexusPipeline.Maintenance", "NexusPipeline.Uninstaller"], True, False,
                  ["@bundled"]),
                 ("unknown.props", ["NexusPipeline.Host", "NexusPipeline.Installer", "NexusPipeline.Launcher", "NexusPipeline.Maintenance", "NexusPipeline.Uninstaller"], True, True,
                  ["@bundled"])]
        for root in (HOST,):
            with production_tools(root) as owner:
                for path, projects, web, desktop, extensions in cases:
                    with self.subTest(repository=root.name, path=path):
                        self.assertEqual(dict(projects=projects, web=web, desktop=desktop, extensions=extensions),
                                         owner.ci_plan.host_scope([path]))

    def test_selects_plugins(self):
        self.declarations()
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                scope = lambda path: owner.ci_plan.plugin_scope(self.repo, [path])["extensions"]
                self.assertEqual([], scope("docs/a.md"))
                self.assertEqual(["maaend"], scope("plugins/specialized/maaend/README.md"))
                self.assertEqual(["live-screenshot"], scope("plugins/managed/live-screenshot/src/Main.cs"))
                self.assertEqual(["game-checkin", "live-screenshot", "maa-framework"],
                                 scope("plugins/managed/frontend-ui/src/a.ts"))
                self.assertEqual(["maa-framework", "maaend", "maas"], scope("adapter-runtime/core/a.js"))
                self.assertEqual(["emulator-support", "game-checkin", "live-screenshot", "maa-framework", "maaend", "maas"],
                                 scope("unknown.json"))

    def test_reads_difference(self):
        self.write("apps/web/first.ts", b"first\n")
        self.commit()
        self.write("apps/desktop/last.cjs", b"last\n")
        head = self.commit()
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                paths, merge = owner.ci_plan.difference(self.repo, self.base_commit, head, head, False)
                self.assertEqual(["apps/desktop/last.cjs", "apps/web/first.ts"], paths)
                self.assertEqual(self.base_commit, merge)
        self.git("mv", "source.txt", "renamed.txt")
        moved = self.commit()
        with production_tools(HOST) as owner:
            paths, _ = owner.ci_plan.difference(self.repo, head, moved, moved, False)
            self.assertEqual(["renamed.txt", "source.txt"], paths)

    def test_rejects_tampering(self):
        self.write("docs/a.md", b"documentation\n")
        head = self.commit()
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                plan = owner.ci_plan.make_plan(self.repo, "host", self.base_commit, head, head)
                owner.ci_plan.verify_plan(self.repo, plan)
                changes = {"source": {}, "changedPaths": [], "checkout": self.base_commit,
                           "nodeRequired": True, "dotnetRequired": True, "partnerRequired": True,
                           "productionRequired": True, "qualification": "local-plan-only", "publishable": True,
                           "repository": "someone/other", "kind": "other", "forbiddenChanges": ["fake"]}
                for field, value in changes.items():
                    altered = deepcopy(plan)
                    altered[field] = value
                    with self.subTest(field=field), self.assertRaises((ValueError, KeyError)):
                        owner.ci_plan.verify_plan(self.repo, altered)

    def test_protects_publisher(self):
        self.declarations()
        base = self.commit()
        protected = ["catalog.json", "metadata/catalog/stable.json", "metadata/releases/stable-state.json", "packages/Alpha/Alpha-0.1.0.zip"]
        for path in protected:
            self.write(path, b"{}\n")
        head = self.commit()
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                real_git = owner.ci_plan.git

                def fixed(repository, *arguments):
                    return (head + "\trefs/heads/develop\n").encode() if arguments[0] == "ls-remote" else real_git(repository, *arguments)

                with patch.object(owner.ci_plan, "git", side_effect=fixed):
                    plan = owner.ci_plan.make_plan(self.repo, "plugins", base, head, head)
                self.assertEqual(protected, plan["forbiddenChanges"])
                with self.assertRaises(ValueError):
                    owner.ci_plan.verify_plan(self.repo, plan)
                plan["forbiddenChanges"] = []
                with self.assertRaises(ValueError):
                    owner.ci_plan.verify_plan(self.repo, plan)

    def test_keeps_empty(self):
        self.write("docs/a.md", b"documentation\n")
        head = self.commit()
        for index, root in enumerate((HOST,)):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                plan = owner.ci_plan.make_plan(self.repo, "host", self.base_commit, head, head)
                real_run = owner.ci_build.subprocess.run

                def git_only(command, *arguments, **keywords):
                    self.assertEqual("git", command[0])
                    return real_run(command, *arguments, **keywords)

                with patch.object(owner.ci_build.subprocess, "run", side_effect=git_only):
                    report = owner.ci_build.run_check(self.repo, "host", plan, None, self.scope / str(index))
                self.assertEqual("PASS", report["status"])
                self.assertEqual([], report["commands"])
                self.assertFalse(report["publishable"])
