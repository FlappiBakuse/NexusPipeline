import hashlib
import io
import urllib.error
from unittest.mock import patch

from ci_fixture import GitFixture
from release_github import GitHub


class ReleaseTransportTests(GitFixture):
    def setUp(self):
        super().setUp()
        self.api = GitHub(token="controlled-fixture-token")
        self.data = b"Original immutable candidate artifact"
        self.digest = hashlib.sha256(self.data).hexdigest()
        self.route = "repos/FlappiBakuse/NexusPipeline/actions/artifacts/41/zip"

    def test_checks_download(self):
        with patch("urllib.request.build_opener") as opener:
            opener.return_value.open.side_effect = lambda *args, **kwargs: io.BytesIO(self.data)
            target = self.scope / "valid.zip"
            self.api.download(self.route, target, len(self.data), self.digest)
            self.assertEqual(self.data, target.read_bytes())
            for name, size, digest in (("digest", len(self.data), "0" * 64),
                                        ("long", len(self.data) - 1, self.digest),
                                        ("short", len(self.data) + 1, self.digest)):
                with self.subTest(name=name), self.assertRaises(ValueError):
                    self.api.download(self.route, self.scope / name, size, digest)
            with self.assertRaises(ValueError):
                self.api.download(self.route, target, len(self.data), self.digest)
            self.assertEqual(self.data, target.read_bytes())

    def test_separates_token(self):
        calls = []
        location = "https://storage.example.invalid/candidate.zip?signature=fixture"

        def open_request(request, **kwargs):
            calls.append(request)
            if request.full_url.startswith("https://api.github.com/"):
                raise urllib.error.HTTPError(request.full_url, 302, "Found", {"Location": location}, None)
            self.assertEqual(location, request.full_url)
            self.assertIsNone(request.get_header("Authorization"))
            return io.BytesIO(self.data)

        with patch("urllib.request.build_opener") as opener:
            opener.return_value.open.side_effect = open_request
            self.api.download(self.route, self.scope / "redirect.zip", len(self.data), self.digest)
        self.assertEqual(2, len(calls))
        self.assertEqual("Bearer controlled-fixture-token", calls[0].get_header("Authorization"))
        self.assertIsNone(calls[1].get_header("X-github-api-version"))

    def test_rejects_redirects(self):
        for index, location in enumerate(("http://storage.example.invalid/file", "https://user:password@storage.example.invalid/file", "/relative")):
            with self.subTest(location=location), patch("urllib.request.build_opener") as opener:
                opener.return_value.open.side_effect = urllib.error.HTTPError("https://api.github.com/", 302, "Found",
                                                                             {"Location": location}, None)
                with self.assertRaises(ValueError):
                    self.api.download(self.route, self.scope / str(index), len(self.data), self.digest)
                self.assertEqual(1, opener.return_value.open.call_count)

    def test_rejects_routes(self):
        with patch("urllib.request.build_opener", side_effect=AssertionError("Unsafe route reached transport")):
            for route in ("https://untrusted.invalid/", "repos/../owner", "other/owner", "repos/owner:secret"):
                with self.subTest(route=route), self.assertRaises(ValueError):
                    self.api.fetch(route)

    def test_bounds_responses(self):
        with patch("urllib.request.build_opener") as opener:
            opener.return_value.open.return_value = io.BytesIO(b'{"value":"too large"}')
            with self.assertRaises(ValueError):
                self.api.request("repos/FlappiBakuse/NexusPipeline", limit=8)

    def test_requires_publisher(self):
        self.api.publisher_token = None
        with patch("urllib.request.build_opener") as opener:
            with self.assertRaisesRegex(ValueError, 'Publisher App'):
                self.api.request('repos/FlappiBakuse/NexusPipeline/releases', 'POST', {})
            opener.assert_not_called()
        self.api.publisher_token = 'controlled-publisher-token'
        requests = []
        with patch("urllib.request.build_opener") as opener:
            def send(request, **kwargs):
                requests.append(request)
                return io.BytesIO(b'{}')
            opener.return_value.open.side_effect = send
            self.api.fetch('repos/FlappiBakuse/NexusPipeline')
            self.api.request('repos/FlappiBakuse/NexusPipeline/releases', 'POST', {})
        self.assertEqual('Bearer controlled-fixture-token', requests[0].get_header('Authorization'))
        self.assertEqual('Bearer controlled-publisher-token', requests[1].get_header('Authorization'))
