"""The transport: one request, its retries, and the envelope it arrives in."""

from __future__ import annotations

import json
import random
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from typing import Any, Callable, Dict, Iterator, List, Optional, Sequence, Tuple

from .errors import VeruApiError, error_from_response
from .types import Meta, decode

DEFAULT_BASE_URL = "https://api.veruapis.com"

# A client library should not be the reason a program runs out of memory
# because something upstream answered with a stream.
_MAX_RESPONSE_BYTES = 32 << 20

_USER_AGENT = "veruapis-python"


class VeruApi:
    """Calls the API.

    Every call is authorised by a key created at veruapis.com and acts as the
    person who created it, limited to the permissions they granted. Nothing
    here widens that key.

        api = VeruApi(api_key=os.environ["VERUAPIS_KEY"])

        for folder in api.mail.list_folders():
            print(folder.name, folder.unread_count)

    The client depends on nothing outside the standard library. That is worth
    more in a library than anywhere else: every dependency here becomes one in
    every program that imports it.
    """

    def __init__(
        self,
        api_key: str,
        *,
        base_url: str = DEFAULT_BASE_URL,
        timeout: float = 30.0,
        max_retries: int = 2,
        opener: Optional[urllib.request.OpenerDirector] = None,
    ) -> None:
        if not api_key or not api_key.strip():
            raise ValueError("An api_key is required. Create one at veruapis.com.")

        self.api_key = api_key.strip()
        self.base_url = base_url.rstrip("/")
        self.timeout = timeout

        # Only 429, 408 and 5xx are ever retried, and only after the delay the
        # server asked for. Zero disables retrying entirely.
        self.max_retries = max(0, max_retries)

        self._opener = opener or urllib.request.build_opener()

        # Injected so the retry behaviour can be tested without the test taking
        # as long as the backoff it is asserting.
        self._sleep: Callable[[float], None] = time.sleep

        from .calendars import CalendarClient
        from .mail import MailClient

        #: Folders, messages, drafts and sending.
        self.mail = MailClient(self)

        #: Calendars, events and availability.
        self.calendar = CalendarClient(self)

    # ------------------------------------------------------------- requests

    def request(
        self,
        method: str,
        path: str,
        *,
        query: Optional[Dict[str, Any]] = None,
        body: Any = None,
        idempotency_key: Optional[str] = None,
    ) -> Tuple[Any, Meta]:
        """Perform one request and return its data and meta.

        Public, because a client is allowed to lag the API: an endpoint nothing
        here wraps is still one call away.
        """
        target = self.base_url + path
        encoded = _query_string(query)
        if encoded:
            target += "?" + encoded

        payload = None
        if body is not None:
            payload = json.dumps(body).encode("utf-8")

        # One key for every attempt, not one per attempt. A retry that
        # generated a new key would be a second request as far as the server is
        # concerned, which defeats the point of sending one.
        if idempotency_key is None and method not in ("GET", "DELETE"):
            idempotency_key = str(uuid.uuid4())

        headers = {
            "Authorization": "Bearer " + self.api_key,
            "Accept": "application/json",
            "User-Agent": _USER_AGENT,
        }
        if payload is not None:
            headers["Content-Type"] = "application/json"
        if idempotency_key:
            headers["Idempotency-Key"] = idempotency_key

        attempt = 0
        while True:
            try:
                status, response_headers, raw = self._send(method, target, headers, payload)
            except urllib.error.URLError as exc:
                # A transport failure. Worth another try, since nothing about
                # the request itself was refused.
                if attempt >= self.max_retries:
                    raise VeruApiError(
                        f"could not reach {self.base_url}: {exc.reason}",
                        code="transport_error",
                    ) from exc
                self._sleep(_backoff(attempt))
                attempt += 1
                continue

            if status < 400:
                return _envelope(status, raw)

            err = error_from_response(status, response_headers, raw)

            # Retried only when the server said to. A 400 or a 403 fails the
            # same way however many times it is sent.
            if not err._retryable or attempt >= self.max_retries:
                raise err

            self._sleep(err.retry_after if err.retry_after else _backoff(attempt))
            attempt += 1

    def _send(
        self,
        method: str,
        target: str,
        headers: Dict[str, str],
        payload: Optional[bytes],
    ) -> Tuple[int, Any, bytes]:
        """One HTTP round trip, with an error response read rather than raised."""
        req = urllib.request.Request(target, data=payload, method=method)
        for name, value in headers.items():
            req.add_header(name, value)

        try:
            with self._opener.open(req, timeout=self.timeout) as res:
                return res.status, res.headers, res.read(_MAX_RESPONSE_BYTES)
        except urllib.error.HTTPError as res:
            # urllib raises on 4xx and 5xx. The body is the API's error
            # envelope, which is the most useful thing in the response.
            with res:
                return res.code, res.headers, res.read(_MAX_RESPONSE_BYTES)

    # ---------------------------------------------------------- pagination

    def paginate(self, path: str, query: Optional[Dict[str, Any]] = None) -> Iterator[Any]:
        """Walk a cursor-paged listing, yielding raw items.

        Cursor rather than an offset: rows arriving mid-walk shift an offset
        and a page gets skipped, which is a data-loss bug that looks like
        nothing at all.
        """
        params = dict(query or {})
        cursor = params.pop("cursor", None)

        while True:
            page_query = dict(params)
            if cursor:
                page_query["cursor"] = cursor

            page, meta = self.request("GET", path, query=page_query)
            page = page or []

            for item in page:
                yield item

            # An empty page carrying a cursor would loop forever, so the
            # absence of rows ends the walk as surely as the absence of one.
            if not meta.next_cursor or not page:
                return
            cursor = meta.next_cursor


def _envelope(status: int, raw: bytes) -> Tuple[Any, Meta]:
    """Split a successful response into its data and its meta."""
    # 204 has no body to decode, and json.loads on nothing raises rather than
    # returning an empty value.
    if status == 204 or not raw:
        return None, Meta()

    parsed = json.loads(raw.decode("utf-8"))
    if not isinstance(parsed, dict):
        return parsed, Meta()

    return parsed.get("data"), decode(Meta, parsed.get("meta") or {})


def _query_string(query: Optional[Dict[str, Any]]) -> str:
    """Render a query, skipping empty values and repeating a list.

    Repeating rather than joining: the API reads ``?folder_id=a&folder_id=b``
    as two values and ``?folder_id=a,b`` as one folder called "a,b".
    """
    if not query:
        return ""

    pairs: List[Tuple[str, str]] = []

    for key, value in query.items():
        # bool before int, because bool is an int and False == 0.
        if value is None or value is False:
            continue
        if value is True:
            pairs.append((key, "true"))
            continue

        if isinstance(value, (list, tuple)):
            pairs.extend((key, str(item)) for item in value if item != "")
            continue

        # An unset number is zero and an unset string is empty. Sending either
        # would ask for something: ?limit=0 is a request for no rows.
        if value == "" or value == 0:
            continue

        pairs.append((key, str(value)))

    return urllib.parse.urlencode(pairs)


def _backoff(attempt: int) -> float:
    """Exponential with jitter.

    Jittered so a fleet of clients does not retry in step and turn a brief
    failure into a sustained one.
    """
    base = min(0.5 * (2 ** attempt), 8.0)
    return base / 2 + random.random() * (base / 2)


def path_escape(value: str) -> str:
    """Escape one path segment. An id with a slash in it is still one id."""
    return urllib.parse.quote(str(value), safe="")
