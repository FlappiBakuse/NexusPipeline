"""Read candidate data without importing or executing its source checkout."""
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import stat
import zipfile

from release_provenance import bind_producer, require, verify_source

MAX_FILES = 8192
MAX_BYTES = 2 * 1024 ** 3


def parse(data):
    require(len(data) <= 8 * 1024 ** 2, "Candidate metadata exceeds its bound")

    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "Duplicate candidate JSON field")
            result[key] = value
        return result

    value = json.loads(data, object_pairs_hook=pairs)
    require(type(value) is dict, "Candidate metadata must be an object")
    return value


def member_path(name):
    require(type(name) is str and 0 < len(name) <= 240 and "\\" not in name and ":" not in name,
            "Unsafe candidate member")
    path = PurePosixPath(name)
    require(not path.is_absolute() and path.as_posix() == name and all(
        part not in ("", ".", "..") and part.rstrip(" .") == part and
        not re.fullmatch(r"(?i)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\..*)?", part) for part in path.parts),
        "Unsafe candidate member")
    return path


def extract(archive_path, destination):
    require(not destination.exists(), "Candidate extraction requires a new external directory")
    for parent in (destination, *destination.parents):
        require(not parent.is_symlink() and not parent.is_junction(), "Candidate path traverses a link")
    with zipfile.ZipFile(archive_path) as archive:
        entries = archive.infolist()
        require(0 < len(entries) <= MAX_FILES, "Candidate member count exceeds its bound")
        names, total = set(), 0
        for item in entries:
            member_path(item.filename)
            require(not item.is_dir() and not item.flag_bits & 1 and
                    stat.S_IFMT(item.external_attr >> 16) in (0, stat.S_IFREG), "Candidate has a non-regular member")
            require(item.filename.casefold() not in names, "Candidate member paths collide")
            names.add(item.filename.casefold())
            total += item.file_size
            require(total <= MAX_BYTES, "Candidate uncompressed bytes exceed their bound")
        destination.mkdir(parents=True)
        for item in entries:
            target = destination.joinpath(*member_path(item.filename).parts)
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(item) as source, target.open("xb") as stream:
                import shutil
                shutil.copyfileobj(source, stream)
    return destination


def inventory(directory):
    values = {}
    for path in directory.rglob("*"):
        require(not path.is_symlink() and not path.is_junction(), "Candidate contains a link")
        if path.is_dir():
            continue
        require(path.is_file(), "Candidate has a non-regular file")
        name = path.relative_to(directory).as_posix()
        member_path(name)
        require(name.casefold() not in {item.casefold() for item in values}, "Candidate member paths collide")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        values[name] = {"path": name, "sizeBytes": path.stat().st_size, "sha256": digest}
        require(len(values) <= MAX_FILES and sum(item["sizeBytes"] for item in values.values()) <= MAX_BYTES,
                "Candidate inventory exceeds its bound")
    return values


def validate(directory, provenance, fetch):
    candidate = bind_producer(parse((directory / "candidate.json").read_bytes()), provenance)
    require(set(candidate) == {"format", "version", "profile", "kind", "producer", "source", "partner", "files", "release"},
            "Candidate fields differ from the current contract")
    require(candidate["kind"] == provenance["kind"], "Candidate channel differs from its verified workflow")
    actual = inventory(directory)
    actual.pop("candidate.json")
    require(type(candidate["files"]) is list and candidate["files"] == [actual[name] for name in sorted(actual)],
            "Candidate file inventory differs from its manifest")
    branch = "develop" if candidate["kind"] == "preview" else "main"
    source = verify_source(fetch, provenance["repository"], candidate["source"], branch)
    partner = candidate["partner"]
    require(type(partner) is dict and set(partner) == {"repository", "identity"}, "Candidate partner is missing")
    opposite = "FlappiBakuse/NexusPipeline-Plugins" if provenance["kind"] == "host" else "FlappiBakuse/NexusPipeline"
    require(partner["repository"] == opposite, "Candidate partner is not the official counterpart")
    verified_partner = verify_source(fetch, opposite, partner["identity"])
    require(type(candidate["release"]) is dict, "Candidate release plan is missing")
    return {"candidate": candidate, "source": source, "partner": verified_partner, "provenance": provenance}
