"""The shapes this API works in.

Written by hand, not generated. A generator produces types you reach through
rather than types you use, and these are short enough to read. The cost of
writing them is drift, so it is paid for: tests/test_spec.py compares every
route this client calls against the API's own description and fails when the
two disagree.

Dataclasses rather than dicts, so ``message.subject`` is a typo an editor
catches and ``message["subject"]`` is not. Unknown fields are ignored on the
way in, so an API that grows a field does not break a client that has not
learned about it yet.
"""

from __future__ import annotations

import dataclasses
import typing
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional

__all__ = [
    "Address",
    "Attachment",
    "Attendee",
    "BusyPeriod",
    "Calendar",
    "Event",
    "Folder",
    "FreeBusy",
    "Message",
    "MessageFlags",
    "MessageSummary",
    "Meta",
    "OutgoingAttachment",
    "SendMessage",
    "Sent",
    "decode",
]


@dataclass(frozen=True)
class Meta:
    """What accompanies every response."""

    request_id: str = ""
    timestamp: str = ""

    # Present on a list that has another page.
    next_cursor: str = ""


@dataclass(frozen=True)
class Address:
    """A person on a message."""

    email: str = ""
    name: str = ""


# ---------------------------------------------------------------------- mail


@dataclass(frozen=True)
class Folder:
    """One mail folder with its counts."""

    id: str = ""
    name: str = ""
    message_count: int = 0
    unread_count: int = 0


@dataclass(frozen=True)
class MessageFlags:
    """The states a message carries."""

    read: bool = False
    starred: bool = False
    answered: bool = False
    draft: bool = False
    deleted: bool = False


@dataclass(frozen=True)
class MessageSummary:
    """A message as a listing returns it.

    No recipients and no body: fetch the message for those. The summary is
    small on purpose, because a folder listing carrying every body would pull a
    whole mailbox through one response.
    """

    id: str = ""
    folder_id: str = ""
    thread_id: str = ""
    message_id: str = ""
    subject: str = ""
    from_: str = ""
    date: str = ""
    size: int = 0
    flags: MessageFlags = field(default_factory=MessageFlags)
    labels: List[str] = field(default_factory=list)


@dataclass(frozen=True)
class Attachment:
    """One part of a message.

    Bytes are a separate call, so reading a message never drags a large file
    through the response.
    """

    part_id: str = ""
    filename: str = ""
    content_type: str = ""
    size: int = 0

    # True for an image the HTML body displays rather than a file to download.
    inline: bool = False


@dataclass(frozen=True)
class Message:
    """One message, fetched.

    MIME traversal, transfer decoding and header decoding are already done.
    """

    id: str = ""
    message_id: str = ""
    subject: str = ""
    date: str = ""
    from_: Optional[Address] = None
    to: List[Address] = field(default_factory=list)
    cc: List[Address] = field(default_factory=list)
    text: str = ""
    html: str = ""
    headers: Dict[str, str] = field(default_factory=dict)
    attachments: List[Attachment] = field(default_factory=list)


@dataclass
class OutgoingAttachment:
    """A file to send."""

    filename: str
    content_type: str

    # The file, base64 encoded. Padded or not, standard or URL-safe.
    content_base64: str

    inline: bool = False

    # Required when inline is true. The HTML refers to it as src="cid:the-id".
    content_id: str = ""


@dataclass
class SendMessage:
    """What sending takes."""

    to: List[str]
    subject: str = ""
    text: str = ""
    html: str = ""
    cc: List[str] = field(default_factory=list)
    bcc: List[str] = field(default_factory=list)
    from_: str = ""
    reply_to: str = ""
    attachments: List[OutgoingAttachment] = field(default_factory=list)


@dataclass(frozen=True)
class Sent:
    """What the API answers when a message is accepted.

    A 202, not a 200: the message is queued and archived in Sent, and delivery
    happens afterwards. That is not a promise it arrived, and this API cannot
    tell you that it did. A failure comes back later as a bounce.
    """

    id: str = ""


# ------------------------------------------------------------------ calendar


@dataclass(frozen=True)
class Calendar:
    """One calendar."""

    id: str = ""
    name: str = ""
    description: str = ""
    color: str = ""

    # False for a calendar somebody shared with this user.
    is_owner: bool = False


@dataclass
class Attendee:
    """Somebody invited."""

    email: str = ""
    name: str = ""

    # REQ-PARTICIPANT, OPT-PARTICIPANT, NON-PARTICIPANT or CHAIR.
    role: str = ""

    # NEEDS-ACTION, ACCEPTED, DECLINED, TENTATIVE or DELEGATED.
    status: str = ""


@dataclass
class Event:
    """One occurrence.

    Times are UTC. The offset a calendar application shows is that
    application's timezone, so anything scheduling by wall-clock time needs the
    user's zone from elsewhere.
    """

    summary: str = ""
    starts_at: str = ""
    ends_at: str = ""

    id: str = ""
    calendar_id: str = ""
    description: str = ""
    location: str = ""
    all_day: bool = False
    status: str = ""
    attendees: List[Attendee] = field(default_factory=list)
    recurrence: List[str] = field(default_factory=list)


@dataclass(frozen=True)
class BusyPeriod:
    """A window somebody is busy in."""

    start: str = ""
    end: str = ""


@dataclass(frozen=True)
class FreeBusy:
    """One person's busy windows.

    Free/busy answers availability without returning event detail, so it needs
    far less access than reading everybody's calendar to work the same thing
    out.
    """

    email: str = ""
    busy: List[BusyPeriod] = field(default_factory=list)


# ------------------------------------------------------------------ decoding


# "from" is a keyword, so the field is spelled from_ and the wire name is
# mapped here rather than everywhere it is used.
_WIRE_NAMES = {"from_": "from"}


def _wire_name(name: str) -> str:
    return _WIRE_NAMES.get(name, name)


def decode(cls: Any, value: Any) -> Any:
    """Build ``cls`` out of decoded JSON, ignoring anything it does not know.

    Ignoring rather than rejecting is deliberate. The API is versioned and adds
    fields within a version; a client that raised on an unfamiliar key would
    break every time the server learned something new.
    """
    if value is None:
        return None

    origin = typing.get_origin(cls)

    if origin is list:
        (item,) = typing.get_args(cls)
        return [decode(item, v) for v in value or []]

    if origin is dict:
        return dict(value or {})

    if origin is typing.Union:  # Optional[X] is Union[X, None]
        args = [a for a in typing.get_args(cls) if a is not type(None)]
        return decode(args[0], value) if len(args) == 1 else value

    if not dataclasses.is_dataclass(cls):
        return value

    if not isinstance(value, dict):
        return value

    hints = typing.get_type_hints(cls)
    kwargs = {}

    for f in dataclasses.fields(cls):
        wire = _wire_name(f.name)
        if wire in value:
            kwargs[f.name] = decode(hints[f.name], value[wire])

    return cls(**kwargs)


def encode(value: Any) -> Any:
    """Turn a dataclass into what goes on the wire.

    Empty values are dropped rather than sent: the API treats an absent field
    and a null one differently on a partial update, and sending every default
    would make every update a full replacement.

    "Empty" means what Go's ``omitempty`` means, which is deliberate. The two
    clients should put the same bytes on the wire for the same call, and this
    is the rule the Go client already follows.

    It has one sharp edge, shared with Go: a false is indistinguishable from an
    unset boolean, so ``all_day=False`` cannot turn an all-day event back into
    a timed one. Recorded in DECISIONS.md rather than fixed here, because
    fixing it in one language and not the others is worse than the edge.
    """
    if dataclasses.is_dataclass(value) and not isinstance(value, type):
        out: Dict[str, Any] = {}
        for f in dataclasses.fields(value):
            item = getattr(value, f.name)
            if item is None or item == "" or item == [] or item == {} or item is False or item == 0:
                continue
            out[_wire_name(f.name)] = encode(item)
        return out

    if isinstance(value, list):
        return [encode(v) for v in value]

    if isinstance(value, dict):
        return {k: encode(v) for k, v in value.items()}

    return value
