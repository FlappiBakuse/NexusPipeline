import json
from unittest.mock import patch

from ci_fixture import GitFixture, HOST, production_tools


class ReleaseSourceTests(GitFixture):
    def test_binds_clean(self):
        for index, root in enumerate((HOST,)):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                receipt = owner.source_snapshot.export_source(self.repo, self.scope / str(index), self.base_commit) \
                    if root == HOST else owner.source_snapshot.snapshot(self.repo, self.scope / str(index), self.base_commit)
                self.assertEqual("git-commit", receipt["sourceMode"])
                self.assertEqual(self.base_commit, receipt["sourceCommit"])
                self.assertEqual(self.git("rev-parse", "HEAD^{tree}").decode().strip(), receipt["sourceTree"])

    def test_keeps_local(self):
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                with patch.dict("os.environ", {"GITHUB_ACTIONS": "true", "GITHUB_RUN_ID": "123", "GITHUB_SHA": self.base_commit}):
                    identity = owner.ci_plan.source_identity(self.repo)
                self.assertEqual("local-uncommitted", identity["sourceMode"])
                self.assertIsNone(identity["sourceCommit"])
                self.assertIsNone(identity["sourceTree"])

    def test_rejects_dirty(self):
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                for path, payload in (("source.txt", b"modified\n"), ("untracked.txt", b"untracked\n")):
                    self.write(path, payload)
                    with self.assertRaises(ValueError):
                        owner.ci_plan.source_identity(self.repo, self.base_commit)
                    if path == "source.txt":
                        self.write(path, b"original\n")
                    else:
                        (self.repo / path).unlink()

    def test_rejects_mismatch(self):
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                for commit in ("1" * 40, self.base_commit[:12], "G" * 40):
                    with self.assertRaises(ValueError):
                        owner.ci_plan.source_identity(self.repo, commit)
                records = owner.source_snapshot.source_files(self.repo)
                records[0]["sha256"] = "0" * 64
                with self.assertRaises(ValueError):
                    owner.git_source.git_identity(self.repo, self.base_commit, records)
                with self.assertRaises(ValueError):
                    owner.git_source.git_identity(self.repo, self.base_commit, [])

    def test_rejects_conversion(self):
        self.git("config", "core.autocrlf", "true")
        self.write("converted.txt", b"converted\r\n")
        commit = self.commit()
        self.assertEqual(b"converted\r\n", (self.repo / "converted.txt").read_bytes())
        self.assertEqual(b"converted\n", self.git("show", commit + ":converted.txt"))
        self.assertEqual(b"", self.git("status", "--porcelain=v1"))
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                with self.assertRaises(ValueError):
                    owner.ci_plan.source_identity(self.repo, commit)

    def test_rejects_gitlinks(self):
        self.git("update-index", "--add", "--cacheinfo", "160000," + self.base_commit + ",module")
        self.git("-c", "user.name=Release Fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "--no-gpg-sign", "-m", "submodule fixture")
        commit = self.git("rev-parse", "HEAD").decode().strip()
        for root in (HOST,):
            with self.subTest(repository=root.name), production_tools(root) as owner:
                with self.assertRaises(ValueError):
                    owner.git_source.git_identity(self.repo, commit, owner.source_snapshot.source_files(self.repo))

    def test_rejects_changed(self):
        with production_tools(HOST) as owner:
            preserve = owner.source_snapshot.preserve_source

            def changed(*arguments):
                result = preserve(*arguments)
                self.write("source.txt", b"changed during export\n")
                return result

            with patch.object(owner.source_snapshot, "preserve_source", side_effect=changed):
                with self.assertRaises(ValueError):
                    owner.source_snapshot.export_source(self.repo, self.scope / "export", self.base_commit)

    def test_clears_identity(self):
        from build_product import update_export
        with production_tools(HOST) as owner:
            output = self.scope / "export"
            owner.source_snapshot.export_source(self.repo, output, self.base_commit)
            receipt = update_export(self.repo, output)
            self.assertEqual("local-uncommitted", receipt["sourceMode"])
            self.assertIsNone(receipt["sourceCommit"])
            self.assertIsNone(receipt["sourceTree"])
            self.assertFalse(receipt["hasUncommittedChanges"])
            self.assertEqual(self.base_commit, receipt["referenceHead"])
