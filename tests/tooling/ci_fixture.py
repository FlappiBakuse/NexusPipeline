"""Owned Git inputs for release source and PR selection behavior."""
from contextlib import contextmanager
import importlib
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest

HOST = Path(__file__).resolve().parents[2]


@contextmanager
def production_tools(root):
    if not (root / "tooling/ci_plan.py").is_file():
        raise ValueError("Production CI source is missing")
    names = ("ci_plan", "ci_build", "ci_assets", "git_source", "source_snapshot", "source_evidence")
    previous = {name: sys.modules.pop(name) for name in names if name in sys.modules}
    sys.path.insert(0, str(root / "tooling"))
    try:
        yield SimpleNamespace(**{name: importlib.import_module(name) for name in names[:5]})
    finally:
        sys.path.pop(0)
        for name in names:
            sys.modules.pop(name, None)
        sys.modules.update(previous)


class GitFixture(unittest.TestCase):
    def setUp(self):
        configured = os.environ.get("NEXUS_TEST_ROOT")
        if not configured:
            self.fail("NEXUS_TEST_ROOT must specify an external owned root")
        self.base = Path(configured).resolve(strict=True)
        self.scope = Path(tempfile.mkdtemp(prefix="ci-", dir=self.base))
        self.repo = self.scope / "repository"
        self.repo.mkdir()
        self.git("init", "--initial-branch=develop")
        self.git("config", "core.autocrlf", "false")
        self.git("config", "core.symlinks", "false")
        self.write("source.txt", b"original\n")
        self.base_commit = self.commit()

    def git(self, *arguments, data=None):
        return subprocess.run(["git", "-C", str(self.repo), *arguments], input=data,
                              capture_output=True, check=True).stdout

    def write(self, relative, data):
        target = self.repo / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        return target

    def commit(self):
        self.git("add", "--all")
        self.git("-c", "user.name=Release Fixture", "-c", "user.email=fixture@example.invalid",
                 "commit", "--no-gpg-sign", "-m", "fixture")
        return self.git("rev-parse", "HEAD").decode().strip()

    def tearDown(self):
        scope = self.scope.resolve(strict=True)
        if scope.parent != self.base or scope.is_symlink() or scope.is_junction():
            raise ValueError("Release fixture cleanup ownership mismatch")
        for path in scope.rglob("*"):
            if path.is_file() and not path.is_symlink():
                path.chmod(0o600)
        shutil.rmtree(scope)
