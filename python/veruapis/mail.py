"""Folders, messages, drafts and sending."""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Optional, Sequence, Tuple, Union

from ._client import path_escape
from .types import (
    Attachment,
    Folder,
    Message,
    MessageSummary,
    Meta,
    SendMessage,
    Sent,
    decode,
    encode,
)


class MailClient:
    """Folders, messages, drafts and sending."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def list_folders(self) -> List[Folder]:
        """Every folder, with its message and unread counts.

        Not paged: a mailbox has tens of folders, so paging would mean writing
        a loop that never runs twice.
        """
        data, _ = self._api.request("GET", "/v1/folders")
        return decode(List[Folder], data)

    def list_messages(
        self,
        *,
        folder_id: Optional[Sequence[str]] = None,
        label: Optional[Sequence[str]] = None,
        flag: str = "",
        unread: bool = False,
        limit: int = 0,
        cursor: str = "",
    ) -> Tuple[List[MessageSummary], Meta]:
        """One page of messages, newest first.

        At least one filter is required: an unfiltered list would be the whole
        mailbox, so the API refuses it rather than quietly returning the inbox.
        Prefer :meth:`messages`, which walks the pages.
        """
        data, meta = self._api.request(
            "GET",
            "/v1/messages",
            query=_message_query(folder_id, label, flag, unread, limit, cursor),
        )
        return decode(List[MessageSummary], data), meta

    def messages(
        self,
        *,
        folder_id: Optional[Sequence[str]] = None,
        label: Optional[Sequence[str]] = None,
        flag: str = "",
        unread: bool = False,
        limit: int = 0,
        cursor: str = "",
    ) -> Iterator[MessageSummary]:
        """Every message matching the filter, following the cursor.

            for message in api.mail.messages(folder_id=[inbox]):
                print(message.subject)

        A failure part way through raises, so a caller that has already
        processed three pages finds out where the walk stopped.
        """
        query = _message_query(folder_id, label, flag, unread, limit, cursor)
        for item in self._api.paginate("/v1/messages", query):
            yield decode(MessageSummary, item)

    def get_message(self, message_id: str) -> Message:
        """One message, with its headers and body decoded."""
        data, _ = self._api.request("GET", "/v1/messages/" + path_escape(message_id))
        return decode(Message, data)

    def list_attachments(self, message_id: str) -> List[Attachment]:
        """A message's attachment metadata.

        Bytes are a separate call, so listing never pulls a large file through
        the response.
        """
        data, _ = self._api.request(
            "GET", "/v1/messages/" + path_escape(message_id) + "/attachments"
        )
        return decode(List[Attachment], data)

    def list_drafts(self, *, limit: int = 0, cursor: str = "") -> Tuple[List[MessageSummary], Meta]:
        """One page of drafts."""
        data, meta = self._api.request(
            "GET", "/v1/drafts", query={"limit": limit, "cursor": cursor}
        )
        return decode(List[MessageSummary], data), meta

    def send(
        self,
        message: Optional[SendMessage] = None,
        *,
        idempotency_key: Optional[str] = None,
        **fields: Any,
    ) -> Sent:
        """Send a message as the key's owner.

        Either shape works, because both read well in different places::

            api.mail.send(to=["ops@example.com"], subject="Nightly", text="All green.")
            api.mail.send(SendMessage(to=["ops@example.com"], subject="Nightly"))

        The API answers 202: the message is queued and archived in Sent, and
        delivery happens afterwards. That is not a promise it arrived, and a
        failure comes back later as a bounce rather than as an error here.

        An Idempotency-Key is generated unless one is supplied, so a retry
        cannot send the same message twice.
        """
        if message is None:
            message = SendMessage(**fields)
        elif fields:
            raise TypeError("pass a SendMessage or keyword fields, not both")

        data, _ = self._api.request(
            "POST",
            "/v1/messages/send",
            body=encode(message),
            idempotency_key=idempotency_key,
        )
        return decode(Sent, data)


def _message_query(
    folder_id: Optional[Sequence[str]],
    label: Optional[Sequence[str]],
    flag: str,
    unread: bool,
    limit: int,
    cursor: str,
) -> Dict[str, Any]:
    return {
        "folder_id": list(folder_id) if folder_id else None,
        "label": list(label) if label else None,
        "flag": flag,
        "unread": unread,
        "limit": limit,
        "cursor": cursor,
    }
