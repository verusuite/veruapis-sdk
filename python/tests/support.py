"""A real HTTP server for the tests to talk to.

A recording server rather than a patched transport. The point of a client
library is what goes on the wire, and a mock that stands in for urllib asserts
that the code calls the mock. This asserts the request the API would actually
receive, which is the only thing that can be wrong.
"""

from __future__ import annotations

import json
import threading
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any, Dict, List, Optional, Tuple
from urllib.parse import parse_qs, urlparse

from veruapis import VeruApi


@dataclass
class Recorded:
    """One request the server received."""

    method: str
    path: str
    query: Dict[str, List[str]]
    headers: Dict[str, str]
    body: Optional[Any]

    @property
    def route(self) -> str:
        return f"{self.method} {self.path}"


@dataclass
class Reply:
    """One response the server should give."""

    status: int = 200
    body: Any = None
    headers: Dict[str, str] = field(default_factory=dict)

    # When set, the body is sent verbatim rather than as JSON, so a test can
    # send something that is not an envelope at all.
    raw: Optional[bytes] = None


class FakeAPI:
    """A server that records what it was asked and answers what it was told."""

    def __init__(self, *replies: Reply) -> None:
        self.replies: List[Reply] = list(replies)
        self.requests: List[Recorded] = []

        # The reply used once the script runs out. An empty envelope keeps a
        # test that only cares about the request from having to describe one.
        self.default = Reply(200, {"data": [], "meta": {"request_id": "req_test"}})

        recorder = self

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *args: Any) -> None:
                pass  # pragma: no cover - silence the test output

            def _handle(self) -> None:
                parsed = urlparse(self.path)

                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""

                # Almost every request is JSON, and one is not: a part of a
                # resumable upload carries the file's bytes. Recording those as
                # bytes rather than failing to parse them is what lets a test
                # assert that the client did not JSON-encode a file.
                if not raw:
                    body = None
                else:
                    try:
                        body = json.loads(raw)
                    except (json.JSONDecodeError, UnicodeDecodeError):
                        # UnicodeDecodeError as well as the JSON one: a part of
                        # an upload is arbitrary bytes, and most files are not
                        # valid UTF-8 at all.
                        body = raw

                recorder.requests.append(
                    Recorded(
                        method=self.command,
                        path=parsed.path,
                        query=parse_qs(parsed.query, keep_blank_values=True),
                        headers={k.lower(): v for k, v in self.headers.items()},
                        body=body,
                    )
                )

                reply = recorder.replies.pop(0) if recorder.replies else recorder.default

                payload = reply.raw
                if payload is None:
                    payload = b"" if reply.body is None else json.dumps(reply.body).encode()

                self.send_response(reply.status)
                for name, value in reply.headers.items():
                    self.send_header(name, value)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                if payload:
                    self.wfile.write(payload)

            do_GET = do_POST = do_PUT = do_PATCH = do_DELETE = _handle

        self._server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self._thread = threading.Thread(
            # serve_forever polls, and the default 500ms is paid on every
            # shutdown. With a server per test that is most of the suite.
            target=lambda: self._server.serve_forever(poll_interval=0.01),
            daemon=True,
        )
        self._thread.start()

    @property
    def base_url(self) -> str:
        host, port = self._server.server_address[:2]
        return f"http://{host}:{port}"

    def client(self, **options: Any) -> VeruApi:
        """A client pointed at this server, with the waiting taken out.

        Retry delays are replaced rather than shortened: a test asserting that
        three attempts happen should not also assert how long a coffee takes.
        """
        api = VeruApi("vak_live_test_secret", base_url=self.base_url, **options)

        self.slept: List[float] = getattr(self, "slept", [])
        api._sleep = self.slept.append
        return api

    @property
    def last(self) -> Recorded:
        return self.requests[-1]

    def close(self) -> None:
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)

    def __enter__(self) -> "FakeAPI":
        return self

    def __exit__(self, *exc: Any) -> None:
        self.close()


def envelope(data: Any, **meta: Any) -> Dict[str, Any]:
    """The shape every successful response arrives in."""
    return {"data": data, "meta": {"request_id": "req_test", "timestamp": "t", **meta}}


def refusal(code: str, message: str = "no", **extra: Any) -> Dict[str, Any]:
    """The shape every refusal arrives in."""
    return {"error": {"code": code, "message": message, "request_id": "req_test", **extra}}
