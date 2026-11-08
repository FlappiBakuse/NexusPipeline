"""Publish immutable Host assets after server provenance and byte readback."""
import hashlib
from pathlib import Path
import re
import urllib.error
import urllib.parse
import zipfile

from legacy_updates import published_policy
from release_provenance import positive, require


def assets(fetch, repository, release_id):
    result = {}
    for page in range(1, 101):
        values = fetch(f"repos/{repository}/releases/{positive(release_id)}/assets?per_page=100&page={page}")
        require(type(values) is list and len(values) <= 100, "Invalid release asset collection")
        for value in values:
            require(type(value) is dict and type(value.get("name")) is str and value["name"] not in result,
                    "Duplicate or malformed release asset")
            positive(value.get("id"))
            result[value["name"]] = value
        if len(values) < 100:
            return result
    raise ValueError("Release asset collection exceeds its pagination budget")


def tag_source(fetch, repository, tag):
    require(re.fullmatch(r"v[0-9]+\.[0-9]+\.[0-9]+", tag), "Invalid Host release tag")
    prefix = "repos/" + repository
    value = fetch(prefix + "/git/ref/tags/" + tag).get("object", {})
    for _ in range(8):
        if value.get("type") == "commit":
            return value.get("sha")
        require(value.get("type") == "tag" and re.fullmatch("[0-9a-f]{40}", str(value.get("sha"))),
                "Release tag is not a commit or annotated tag")
        value = fetch(prefix + "/git/tags/" + value["sha"]).get("object", {})
    raise ValueError("Release tag indirection exceeds its bound")


def host_plan(directory, verified):
    candidate = verified["candidate"]
    require(candidate["kind"] == "host", "Host publisher requires a Host candidate")
    plan = candidate["release"]
    require(set(plan) == {"tag", "assets"} and plan["tag"] == "v0.18.0", "Unexpected Host release plan")
    names = {"NexusPipeline-v0.18.0-win-x64-" + suffix for suffix in ("Setup.exe", "Portable.zip", "Update.zip")}
    files = {item["path"]: item for item in candidate["files"]}
    require(type(plan["assets"]) is list and len(plan["assets"]) == 3 and set(plan["assets"]) == names and
            set(files) == names, "Host candidate must contain exactly the three public assets")
    for name in sorted(names):
        path = directory / name
        if name.endswith(".exe"):
            with path.open("rb") as stream:
                require(stream.read(2) == b"MZ", "Setup is not a Windows executable")
        else:
            with zipfile.ZipFile(path) as archive:
                entries = archive.namelist()
                require(len(entries) == len(set(item.casefold() for item in entries)) and archive.testzip() is None,
                        "Host ZIP contains repeated or damaged members")
                required = {"NexusPipeline.exe", "NexusPipeline.update.exe", "README.md",
                            "resources/payload-manifest.json", "resources/desktop/NexusPipeline.Launcher.exe", "package-info/recipe.json"}
                require(required <= set(entries) and not any(item.startswith("application/") for item in entries),
                        "Host ZIP differs from the public payload layout")
    return plan, files


def readback(api, repository, release_id, expected, record, output):
    identity = positive(record.get("id"))
    current = api.fetch(f"repos/{repository}/releases/assets/{identity}")
    require(current.get("id") == identity and current.get("name") == expected["path"] and
            current.get("state") == "uploaded" and current.get("size") == expected["sizeBytes"] and
            current.get("digest") == "sha256:" + expected["sha256"], "Release asset server identity differs")
    destination = output / (str(identity) + "-" + expected["sha256"])
    api.download(f"repos/{repository}/releases/assets/{identity}", destination, expected["sizeBytes"], expected["sha256"])
    with destination.open("rb") as stream:
        require(hashlib.file_digest(stream, "sha256").hexdigest() == expected["sha256"] and
                destination.stat().st_size == expected["sizeBytes"], "Release asset readback bytes differ")
    return {"id": identity, "name": current["name"], "sizeBytes": current["size"], "sha256": expected["sha256"]}


def publish_host(api, directory, verified, output, publish=False):
    plan, files = host_plan(directory, verified)
    repository = verified["provenance"]["repository"]
    source = verified["source"]["sourceCommit"]
    require(tag_source(api.fetch, repository, plan["tag"]) == source, "Existing release tag differs from candidate source")
    barrier = published_policy(api.fetch)
    result = {"status": "VERIFIED_NOT_PUBLISHED", "remoteWritten": False, "barrier": barrier,
              "sourceCommit": source, "tag": plan["tag"]}
    if not publish:
        return result
    prefix = "repos/" + repository + "/releases"
    try:
        release = api.fetch(prefix + "/tags/" + urllib.parse.quote(plan["tag"], safe=""))
    except urllib.error.HTTPError as error:
        if error.code != 404:
            raise
        release = api.request(prefix, "POST", {"tag_name": plan["tag"], "target_commitish": source,
            "name": "NexusPipeline " + plan["tag"], "draft": True, "prerelease": True,
            "body": "Install 0.18 in a new directory. See docs/users/install-018.md."})
    release_id = positive(release.get("id"))
    require(release.get("tag_name") == plan["tag"] and release.get("prerelease") is True,
            "Release identity differs from the candidate")
    current = assets(api.fetch, repository, release_id)
    require(set(current) <= set(files) and (release.get("draft") is True or set(current) == set(files)),
            "Release contains unexpected assets or an incomplete public asset set")
    verified_assets = []
    for name in sorted(files):
        record = current.get(name)
        if record is None:
            record = api.upload(repository, release_id, directory / name, name)
        verified_assets.append(readback(api, repository, release_id, files[name], record, output / "first"))
    final = assets(api.fetch, repository, release_id)
    require(set(final) == set(files), "Release public whitelist changed during upload")
    for name in sorted(files):
        original = next(item for item in verified_assets if item["name"] == name)
        require(final[name].get("id") == original["id"], "Release asset identity changed after upload")
        readback(api, repository, release_id, files[name], final[name], output / "final")
    require(tag_source(api.fetch, repository, plan["tag"]) == source, "Release tag changed before publication")
    published_policy(api.fetch)
    api.request(prefix + "/" + str(release_id), "PATCH", {"draft": False, "prerelease": True})
    published = api.fetch(prefix + "/" + str(release_id))
    require(published.get("id") == release_id and published.get("tag_name") == plan["tag"] and
            published.get("draft") is False and published.get("prerelease") is True,
            "Release did not reach its public prerelease state")
    return {**result, "status": "PUBLISHED", "remoteWritten": True, "releaseId": release_id, "assets": verified_assets}
