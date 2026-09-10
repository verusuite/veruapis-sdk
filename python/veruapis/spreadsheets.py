"""The contents of a spreadsheet: ranges, appends and structural changes.

A spreadsheet's lifecycle — creating, renaming, trashing — is documents,
because a spreadsheet is a document. This module is its cells.

Worth knowing before a key is granted: writing values needs
``sheets.values.write`` and merges cleanly with whatever else is happening in
the document, while inserting or deleting rows needs ``sheets.structure.write``
and moves everything below them. A script that fills in a weekly figure should
hold only the first.
"""

from __future__ import annotations

from typing import Any, Dict, List, Optional, Sequence

from ._client import path_escape
from .types import (
    DocumentState,
    StructureResult,
    ValueRange,
    Workbook,
    WriteResult,
    decode,
)

#: What is stored in the cell, so a formula comes back as its own text. The
#: default, and it always works.
RENDER_STORED = "stored"

#: Evaluated results. Costs a pass over the whole workbook rather than over the
#: range, because a formula in it can depend on a cell far outside it, and it is
#: unavailable when no formula engine is running.
RENDER_COMPUTED = "computed"


class SpreadsheetsClient:
    """Reading and writing a workbook's cells."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def get(self, spreadsheet_id: str) -> Workbook:
        """The workbook: every sheet with its extent and merges.

        This is how the tabs are listed; there is no separate call for them.
        Read it first to learn the sheet names an A1 range needs.
        """
        data, _ = self._api.request("GET", _sheet_path(spreadsheet_id))
        return decode(Workbook, data)

    def state(self, spreadsheet_id: str) -> DocumentState:
        """The document's change token.

        Two uses, one value: poll it to notice somebody else's change — there
        are no webhooks for spreadsheets — and pass it to
        :meth:`apply_structure`, which refuses to work without it.
        """
        data, _ = self._api.request("GET", _sheet_path(spreadsheet_id) + "/state")
        return decode(DocumentState, data)

    def values(
        self,
        spreadsheet_id: str,
        range: str,
        *,
        render: str = RENDER_STORED,
    ) -> ValueRange:
        """Read one A1 range."""
        data, _ = self._api.request(
            "GET",
            _values_path(spreadsheet_id, range),
            query=_render_query(render),
        )
        return decode(ValueRange, data)

    def batch_values(
        self,
        spreadsheet_id: str,
        ranges: Sequence[str],
        *,
        render: str = RENDER_STORED,
    ) -> List[ValueRange]:
        """Read several ranges in one call.

        The cell cap applies to the whole call, and a computed read evaluates
        the workbook once for all of them rather than once per range.
        """
        data, _ = self._api.request(
            "POST",
            _sheet_path(spreadsheet_id) + "/values/batch-get",
            query=_render_query(render),
            body={"ranges": list(ranges)},
        )
        return decode(List[ValueRange], (data or {}).get("value_ranges"))

    def write(
        self,
        spreadsheet_id: str,
        range: str,
        values: Sequence[Sequence[Any]],
    ) -> WriteResult:
        """Overwrite one range.

        Values are anchored at the range's top-left corner, and a block shorter
        than the range leaves the rest untouched: writing two rows into a
        ten-row range writes two rows. Clearing is a separate call for exactly
        that reason.

        An empty string deletes a cell rather than storing a blank, which would
        keep it inside the sheet's used extent and widen every unbounded range
        from then on.
        """
        data, _ = self._api.request(
            "PUT",
            _values_path(spreadsheet_id, range),
            body={"values": [list(row) for row in values]},
        )
        return decode(WriteResult, data)

    def batch_write(
        self,
        spreadsheet_id: str,
        data: Sequence[ValueRange],
    ) -> WriteResult:
        """Write several ranges as one document write.

        Somebody with the sheet open sees one change rather than a flicker of
        several.
        """
        body = [{"range": item.range, "values": item.values} for item in data]
        out, _ = self._api.request(
            "POST",
            _sheet_path(spreadsheet_id) + "/values/batch-update",
            body={"data": body},
        )
        return decode(WriteResult, out)

    def append(
        self,
        spreadsheet_id: str,
        range: str,
        values: Sequence[Sequence[Any]],
    ) -> WriteResult:
        """Add rows after the last populated row of the range's own columns.

        Of its own columns, not the sheet's: an unrelated note in column Z does
        not push the table down. This is the call a log or a nightly export
        wants — it needs no read first, and two appends cannot overwrite each
        other.
        """
        data, _ = self._api.request(
            "POST",
            _values_path(spreadsheet_id, range) + "/append",
            body={"values": [list(row) for row in values]},
        )
        return decode(WriteResult, data)

    def clear(self, spreadsheet_id: str, range: str) -> WriteResult:
        """Empty a range and leave its formatting.

        A template keeps its headers, colours and number formats.
        """
        data, _ = self._api.request(
            "POST", _values_path(spreadsheet_id, range) + "/clear"
        )
        return decode(WriteResult, data)

    def apply_structure(
        self,
        spreadsheet_id: str,
        state: str,
        requests: Sequence[Dict[str, Any]],
    ) -> StructureResult:
        """Insert and delete rows, columns and sheets; merge, sort and format.

        ``state`` comes from :meth:`state` and is required: these are the
        changes that move data other requests address by position, so one
        applied to a document that has moved on merges cleanly into a corrupt
        grid. A stale token is refused with a conflict — read the state again
        and reapply.

        Requests apply in order and each sees the effect of the one before, so
        adding a sheet and formatting it in one batch works::

            st = api.spreadsheets.state(doc)
            api.spreadsheets.apply_structure(doc, st.state, [
                {"add_sheet": {"title": "Q4"}},
                {"repeat_cell": {"range": "Q4!A1:D1", "style": {"b": 1}}},
            ])
        """
        data, _ = self._api.request(
            "POST",
            _sheet_path(spreadsheet_id) + "/batch-update",
            body={"requests": list(requests)},
            if_match=state,
        )
        return decode(StructureResult, data)


def _sheet_path(spreadsheet_id: str) -> str:
    return "/v1/spreadsheets/" + path_escape(spreadsheet_id)


def _values_path(spreadsheet_id: str, range: str) -> str:
    """Build a range path, escaping the range as one segment.

    A1 notation carries characters a URL reads as structure — a quoted sheet
    name, a space, a colon — and a range is never a path.
    """
    return _sheet_path(spreadsheet_id) + "/values/" + path_escape(range)


def _render_query(render: str) -> Optional[Dict[str, Any]]:
    if not render or render == RENDER_STORED:
        return None
    return {"value_render": render}
