"""Bind an explicit clean commit to the original bytes exported by the source owner."""
import hashlib
import io
import re
import subprocess


def git(root, *arguments, data=None):
    return subprocess.run(["git", "-C", str(root), *arguments], input=data, capture_output=True, check=True).stdout


def clean_commit(root, commit):
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise ValueError("Source commit must be a full lowercase Git SHA")
    if git(root, "rev-parse", "--verify", "HEAD").decode().strip() != commit:
        raise ValueError("Checkout does not match the fixed source commit")
    if git(root, "status", "--porcelain=v1", "--untracked-files=all"):
        raise ValueError("Git source requires a clean tracked and untracked worktree")
    return git(root, "rev-parse", commit + "^{tree}").decode().strip()


def git_identity(root, commit, records):
    tree = clean_commit(root, commit)
    entries = {}
    for entry in git(root, "ls-tree", "-r", "-z", "--full-tree", commit).split(b"\0"):
        if not entry:
            continue
        metadata, path = entry.split(b"\t", 1)
        mode, kind, object_id = metadata.split()
        if mode not in (b"100644", b"100755") or kind != b"blob":
            raise ValueError("Git source supports regular files only")
        entries[path.decode("utf-8")] = object_id
    if set(entries) != {item["path"] for item in records}:
        raise ValueError("Export membership differs from the fixed Git tree")
    ordered = sorted(records, key=lambda item: item["path"].encode("utf-8"))
    requested = b"".join(entries[item["path"]] + b"\n" for item in ordered)
    stream = io.BytesIO(git(root, "cat-file", "--batch", data=requested))
    for item in ordered:
        header = stream.readline().split()
        if len(header) != 3 or header[:2] != [entries[item["path"]], b"blob"]:
            raise ValueError("Git source object is unavailable")
        size = int(header[2])
        data = stream.read(size)
        if size != item["sizeBytes"] or len(data) != size or stream.read(1) != b"\n" or \
                hashlib.sha256(data).hexdigest() != item["sha256"]:
            raise ValueError("Export bytes differ from the fixed Git tree: " + item["path"])
    if stream.read() or clean_commit(root, commit) != tree:
        raise ValueError("Git source changed while its bytes were verified")
    return {"sourceMode": "git-commit", "sourceCommit": commit, "sourceTree": tree}
