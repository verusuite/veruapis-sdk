"""The client, checked against the API's own description.

Nothing here is generated, so this is what keeps the hand-written client
honest. A route this client calls that the API does not serve is a 404 waiting
for whoever calls that method, and it fails the build.

The routes are collected by *calling every method* against a recording server,
not by scanning the source. The Go client learned this the hard way: a path
built by concatenation gives up only its first literal to a regular
expression, so real routes were reported as unwrapped and the check passed on
truncated prefixes that happened to match. Running the client is exact, and it
exercises every method as a side effect.

It reads the JSON rather than the YAML so the package needs no dependency for
it. Both are written by one run of ``veruapis spec`` and cannot disagree.
"""

from __future__ import annotations

import json
import os
import re
import unittest
from pathlib import Path
from typing import Dict, List, Set

from support import FakeAPI, Reply

from veruapis import Event, SendMessage, VeruApi

SPEC = Path(__file__).resolve().parents[2] / "spec" / "openapi.json"

# The values every_call passes, so a concrete path can be turned back into the
# templated one the specification describes.
IDS = {"f1", "m1", "c1", "e1"}


def every_call(api: VeruApi) -> None:
    """One invocation of every method this client offers.

    Adding a method here is the price of adding one to the client, and it is
    the right price: an unlisted method is one nothing has ever called, which
    is how a typo in a path ships.
    """
    api.mail.list_folders()
    api.mail.list_messages(folder_id=["f1"])
    api.mail.get_message("m1")
    api.mail.list_attachments("m1")
    api.mail.list_drafts(limit=10)
    api.mail.send(SendMessage(to=["a@b.example"]))

    api.calendar.list_calendars()
    api.calendar.get_calendar("c1")
    api.calendar.list_events(start="s", end="e")
    api.calendar.get_event("c1", "e1")
    api.calendar.create_event("c1", Event(summary="x"))
    api.calendar.update_event("c1", "e1", Event(summary="y"))
    api.calendar.delete_event("c1", "e1")
    api.calendar.free_busy(["a@b.example"], "s", "e")

    # The iterators, drained so their first request is made.
    for _ in api.mail.messages(folder_id=["f1"]):
        break
    for _ in api.calendar.events(start="s", end="e"):
        break


def templated(path: str) -> str:
    """Turn /v1/messages/m1 into /v1/messages/{}, the shape the spec uses."""
    parts = ["{}" if p in IDS else p for p in path.split("/")]
    return "/".join(parts).rstrip("/")


def routes_the_client_calls() -> Set[str]:
    api = FakeAPI()
    try:
        # An empty list satisfies every return shape here, and an absent cursor
        # stops the iterators after one page.
        api.default = Reply(200, {"data": [], "meta": {"request_id": "r"}})
        every_call(api.client(max_retries=0))
        return {f"{r.method} {templated(r.path)}" for r in api.requests}
    finally:
        api.close()


def documented() -> Set[str]:
    """Every route the API serves, minus the ones it describes but does not."""
    spec = json.loads(SPEC.read_text(encoding="utf-8"))

    out = set()
    for path, item in (spec.get("paths") or {}).items():
        for method, operation in item.items():
            if method.upper() not in ("GET", "POST", "PUT", "PATCH", "DELETE"):
                continue
            # A route the server does not implement yet is marked deprecated in
            # the description. The SDK should not offer it, so it does not
            # count as documented for this purpose.
            if isinstance(operation, dict) and operation.get("deprecated"):
                continue
            normalised = re.sub(r"\{[^}]*\}", "{}", path).rstrip("/")
            out.add(f"{method.upper()} {normalised}")
    return out


class SpecTest(unittest.TestCase):
    def setUp(self) -> None:
        if not SPEC.exists():  # pragma: no cover - only on a partial checkout
            self.skipTest(
                f"no specification at {SPEC}; regenerate with\n"
                "  cd ../../veruapis && go run ./cmd/veruapis spec ../veruapis-sdks/spec/openapi.yaml"
            )
        self.spec = documented()
        self.assertTrue(self.spec, "the specification describes no paths")

    def test_every_route_the_client_calls_is_documented(self) -> None:
        unknown = sorted(routes_the_client_calls() - self.spec)

        self.assertEqual(
            unknown,
            [],
            "these are 404s waiting to happen. Fix the client, or regenerate the "
            "specification if the API really did change.",
        )

    def test_endpoints_not_wrapped_yet(self) -> None:
        # Reported, not failed: the client is allowed to lag, and request()
        # reaches anything it has not wrapped.
        missing = sorted(self.spec - routes_the_client_calls())
        if missing:
            print(f"\n{len(missing)} endpoint(s) not wrapped yet (request() reaches them):")
            for route in missing:
                print(f"  {route}")

    def test_the_client_covers_what_the_other_clients_cover(self) -> None:
        """Consistency, asserted rather than hoped for.

        Four clients drifting apart is the failure this repository exists to
        prevent, and the Go client is the one the others were written against.
        If Go grows a method, this should grow one too.
        """
        go_source = (SPEC.parents[1] / "go").glob("*.go")
        called = set()
        for path in go_source:
            if path.name.endswith("_test.go"):
                continue
            for match in re.finditer(r'Path:\s*"([^"]+)"', path.read_text(encoding="utf-8")):
                called.add(match.group(1).rstrip("/"))

        # Only the fixed prefixes are comparable, since Go builds the rest by
        # concatenation. A prefix this client never touches is a gap.
        mine = {templated(r.split(" ", 1)[1]) for r in routes_the_client_calls()}
        for route in sorted(called):
            with self.subTest(route=route):
                self.assertTrue(
                    any(m.startswith(route.rstrip("/")) for m in mine),
                    f"the Go client calls {route} and this one never does",
                )


if __name__ == "__main__":
    unittest.main()
