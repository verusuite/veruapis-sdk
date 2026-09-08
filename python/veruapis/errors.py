"""The one error this client raises."""

from __future__ import annotations

import email.utils
import time
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional


@dataclass(frozen=True)
class ErrorDetail:
    """One field the API rejected."""

    field: str
    message: str = ""


class VeruApiError(Exception):
    """A refusal from the API.

    Branch on ``code``, never on ``message``. The code is a stable identifier;
    the message is written for a person and may be reworded at any time.

        try:
            api.mail.send(...)
        except VeruApiError as err:
            if err.is_permission_problem:
                raise SystemExit(f"this key is missing {err.code}")
    """

    def __init__(
        self,
        message: str,
        *,
        code: str = "unknown_error",
        status: int = 0,
        request_id: str = "",
        details: Optional[List[ErrorDetail]] = None,
        retry_after: Optional[float] = None,
    ) -> None:
        super().__init__(message)

        self.message = message
        self.code = code
        self.status = status
        self.request_id = request_id
        self.details = details or []

        # Seconds the API asked the caller to wait, when it said. None when it
        # did not, which is different from zero.
        self.retry_after = retry_after

    def __str__(self) -> str:
        if self.request_id:
            return f"{self.message} ({self.code}, request {self.request_id})"
        return f"{self.message} ({self.code})"

    def __repr__(self) -> str:  # pragma: no cover - debugging aid
        return f"VeruApiError(code={self.code!r}, status={self.status}, message={self.message!r})"

    # The four questions worth asking about a failure. Properties rather than
    # methods, because that is what Python reads as: `if err.is_not_found`.

    @property
    def is_auth_problem(self) -> bool:
        """The credential was missing, malformed, or not one this API issues."""
        return self.status == 401

    @property
    def is_permission_problem(self) -> bool:
        """The key is valid but was not granted a permission this call needs.

        Creating a new key with the right permission is the fix. Retrying is
        not, which is why this is never retried.
        """
        return self.status == 403

    @property
    def is_not_found(self) -> bool:
        """The record does not exist, or belongs to another workspace.

        The API does not distinguish the two, deliberately.
        """
        return self.status == 404

    @property
    def is_rate_limited(self) -> bool:
        """The plan's rate limit or daily quota is spent.

        The client already waits and retries. This is for a caller that would
        rather slow itself down than be slowed.
        """
        return self.status == 429

    @property
    def _retryable(self) -> bool:
        """Whether sending the same request again could succeed.

        A 400 or a 403 fails the same way however many times it is sent.
        """
        return self.status in (408, 429) or self.status >= 500


def error_from_response(status: int, headers: Any, body: bytes) -> VeruApiError:
    """Read the API's error envelope out of a failed response."""
    retry_after = _retry_after(headers)

    envelope: Dict[str, Any] = {}
    try:
        import json

        parsed = json.loads(body.decode("utf-8", "replace"))
        if isinstance(parsed, dict):
            envelope = parsed.get("error") or {}
    except (ValueError, AttributeError):
        envelope = {}

    if not isinstance(envelope, dict) or not envelope.get("code"):
        # Something upstream of the API answered: a proxy, an error page, a
        # misrouted request. Saying so beats reporting a parse failure nobody
        # can act on.
        return VeruApiError(
            f"the server answered {status}",
            code="unreadable_response",
            status=status,
            retry_after=retry_after,
        )

    details = [
        ErrorDetail(field=d.get("field", ""), message=d.get("message", ""))
        for d in envelope.get("details") or []
        if isinstance(d, dict)
    ]

    return VeruApiError(
        envelope.get("message") or f"the server answered {status}",
        code=envelope["code"],
        status=status,
        request_id=envelope.get("request_id") or "",
        details=details,
        retry_after=retry_after,
    )


def _retry_after(headers: Any) -> Optional[float]:
    """Read Retry-After, which the RFC allows as seconds or as a date."""
    if headers is None:
        return None

    raw = headers.get("Retry-After") if hasattr(headers, "get") else None
    if not raw:
        return None

    raw = str(raw).strip()

    try:
        seconds = float(raw)
        return seconds if seconds > 0 else None
    except ValueError:
        pass

    try:
        when = email.utils.parsedate_to_datetime(raw)
    except (TypeError, ValueError):
        # Neither a number nor a date. Something upstream wrote the header and
        # the backoff is a better answer than crashing on it.
        return None

    wait = when.timestamp() - time.time()
    return wait if wait > 0 else None
