"""Bounded GitHub REST transport used by the protected publication owner."""
import hashlib
import json
import os
from pathlib import Path
import urllib.error
import urllib.parse
import urllib.request

from release_provenance import positive, require

API_VERSION = "2026-03-10"


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, target):
        return None


class GitHub:
    def __init__(self, token=None):
        self.token = token if token is not None else os.environ.get("GH_TOKEN")
        self.publisher_token = os.environ.get("NEXUS_PUBLISHER_TOKEN")
        require(bool(self.token), "A scoped GitHub token is required")

    def request(self, path, method="GET", data=None, accept="application/vnd.github+json", limit=8388608):
        require(type(path) is str and path.startswith("repos/") and ":" not in path and ".." not in path,
                "Invalid GitHub API route")
        token = self.token if method == "GET" else self.publisher_token
        require(bool(token), "A scoped Publisher App token is required for remote writes")
        headers = {"Accept": accept, "Authorization": "Bearer " + token,
                   "X-GitHub-Api-Version": API_VERSION, "User-Agent": "NexusPipeline-Publisher"}
        payload = None if data is None else json.dumps(data, separators=(",", ":")).encode()
        if payload is not None:
            headers["Content-Type"] = "application/json"
        request = urllib.request.Request("https://api.github.com/" + path, data=payload, headers=headers, method=method)
        with urllib.request.build_opener(NoRedirect).open(request, timeout=60) as response:
            content = response.read(limit + 1)
        require(len(content) <= limit, "GitHub JSON response exceeds its bound")
        return json.loads(content) if content else None

    def fetch(self, path):
        return self.request(path)

    def download(self, path, destination, size, digest):
        require(type(size) is int and 0 < size <= 2 * 1024 ** 3, "Invalid download size")
        require(type(digest) is str and len(digest) == 64, "Missing download SHA256")
        require(not destination.exists(), "Download destination already exists")
        headers = {"Accept": "application/octet-stream", "Authorization": "Bearer " + self.token,
                   "X-GitHub-Api-Version": API_VERSION, "User-Agent": "NexusPipeline-Publisher"}
        require(path.startswith("repos/") and ":" not in path and ".." not in path, "Invalid download API route")
        request = urllib.request.Request("https://api.github.com/" + path, headers=headers)
        try:
            response = urllib.request.build_opener(NoRedirect).open(request, timeout=60)
        except urllib.error.HTTPError as error:
            require(error.code == 302, "GitHub download did not return a supported response")
            target = error.headers.get("Location", "")
            parsed = urllib.parse.urlsplit(target)
            require(parsed.scheme == "https" and parsed.hostname and not parsed.username and not parsed.password,
                    "GitHub download redirect is unsafe")
            # The signed storage request carries no repository token.
            response = urllib.request.build_opener(NoRedirect).open(
                urllib.request.Request(target, headers={"User-Agent": "NexusPipeline-Publisher"}), timeout=120)
        actual = hashlib.sha256()
        total = 0
        destination.parent.mkdir(parents=True, exist_ok=True)
        with response, destination.open("xb") as stream:
            while block := response.read(1024 * 1024):
                total += len(block)
                require(total <= size, "GitHub download exceeds its declared size")
                stream.write(block)
                actual.update(block)
        require(total == size and actual.hexdigest() == digest, "GitHub download bytes differ from server identity")
        return destination

    def upload(self, repository, release_id, path, name):
        require(bool(self.publisher_token), "A scoped Publisher App token is required for asset uploads")
        positive(release_id)
        require(repository in ("FlappiBakuse/NexusPipeline", "FlappiBakuse/NexusPipeline-Plugins"), "Unknown release owner")
        data = Path(path).read_bytes()
        require(0 < len(data) <= 2 * 1024 ** 3, "Release asset exceeds its upload bound")
        url = f"https://uploads.github.com/repos/{repository}/releases/{release_id}/assets?name=" + urllib.parse.quote(name, safe="")
        request = urllib.request.Request(url, data=data, method="POST", headers={
            "Authorization": "Bearer " + self.publisher_token, "Accept": "application/vnd.github+json",
            "X-GitHub-Api-Version": API_VERSION, "Content-Type": "application/octet-stream",
            "User-Agent": "NexusPipeline-Publisher"})
        with urllib.request.build_opener(NoRedirect).open(request, timeout=120) as response:
            result = response.read(1024 * 1024 + 1)
        require(len(result) <= 1024 * 1024, "Upload response exceeds its bound")
        return json.loads(result)
