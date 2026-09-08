"""Calendars, events and availability.

The module is ``calendars`` rather than ``calendar`` so that nothing in a
program importing this ever has to think about the standard library module of
that name. The attribute is still ``api.calendar``, because that is what it is.
"""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Optional, Sequence, Tuple

from ._client import path_escape
from .types import Calendar, Event, FreeBusy, Meta, decode, encode


class CalendarClient:
    """Calendars, events and availability."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def list_calendars(self) -> List[Calendar]:
        """The calendars this user can see."""
        data, _ = self._api.request("GET", "/v1/calendars")
        return decode(List[Calendar], data)

    def get_calendar(self, calendar_id: str) -> Calendar:
        """One calendar."""
        data, _ = self._api.request("GET", "/v1/calendars/" + path_escape(calendar_id))
        return decode(Calendar, data)

    def list_events(
        self,
        *,
        start: str,
        end: str,
        calendar_id: Optional[Sequence[str]] = None,
        limit: int = 0,
        cursor: str = "",
    ) -> Tuple[List[Event], Meta]:
        """One page of events across every calendar in a window.

        Recurring events arrive already expanded, one entry per occurrence,
        which is why the window is required rather than optional: a series
        expands without limit, so there is no finite answer to "all events".
        """
        data, meta = self._api.request(
            "GET",
            "/v1/events",
            query=_event_query(start, end, calendar_id, limit, cursor),
        )
        return decode(List[Event], data), meta

    def events(
        self,
        *,
        start: str,
        end: str,
        calendar_id: Optional[Sequence[str]] = None,
        limit: int = 0,
        cursor: str = "",
    ) -> Iterator[Event]:
        """Every event in the window, following the cursor."""
        query = _event_query(start, end, calendar_id, limit, cursor)
        for item in self._api.paginate("/v1/events", query):
            yield decode(Event, item)

    def get_event(self, calendar_id: str, event_id: str) -> Event:
        """One event."""
        data, _ = self._api.request(
            "GET",
            "/v1/calendars/" + path_escape(calendar_id) + "/events/" + path_escape(event_id),
        )
        return decode(Event, data)

    def create_event(
        self,
        calendar_id: str,
        event: Event,
        *,
        idempotency_key: Optional[str] = None,
    ) -> Event:
        """Add an event to a calendar.

        Times are UTC. An Idempotency-Key is generated unless one is supplied,
        so a retried booking cannot create a second event.
        """
        data, _ = self._api.request(
            "POST",
            "/v1/calendars/" + path_escape(calendar_id) + "/events",
            body=encode(event),
            idempotency_key=idempotency_key,
        )
        return decode(Event, data)

    def update_event(self, calendar_id: str, event_id: str, changes: Event) -> Event:
        """Amend an event. Only the fields set are changed."""
        data, _ = self._api.request(
            "PATCH",
            "/v1/calendars/" + path_escape(calendar_id) + "/events/" + path_escape(event_id),
            body=encode(changes),
        )
        return decode(Event, data)

    def delete_event(self, calendar_id: str, event_id: str) -> None:
        """Remove an event.

        Cancelling notifies the attendees; this does not. Use the one that
        matches what actually happened.
        """
        self._api.request(
            "DELETE",
            "/v1/calendars/" + path_escape(calendar_id) + "/events/" + path_escape(event_id),
        )

    def free_busy(self, emails: Sequence[str], start: str, end: str) -> List[FreeBusy]:
        """When these people are busy, without returning event detail.

        The right call for availability: it needs far less access than reading
        everybody's calendar to work the same thing out.
        """
        data, _ = self._api.request(
            "POST",
            "/v1/freebusy",
            body={"emails": list(emails), "start": start, "end": end},
        )
        return decode(List[FreeBusy], data)


def _event_query(
    start: str,
    end: str,
    calendar_id: Optional[Sequence[str]],
    limit: int,
    cursor: str,
) -> Dict[str, Any]:
    return {
        "start": start,
        "end": end,
        "calendar_id": list(calendar_id) if calendar_id else None,
        "limit": limit,
        "cursor": cursor,
    }
