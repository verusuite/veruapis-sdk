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

# -------------------------------------------------------------- spreadsheets


@dataclass(frozen=True)
class Merge:
    """One merged rectangle, zero-based and inclusive at both corners."""

    id: str = ""
    c0: int = 0
    r0: int = 0
    c1: int = 0
    r1: int = 0


@dataclass(frozen=True)
class Sheet:
    """One tab, with the extent of what is actually on it."""

    id: str = ""
    name: str = ""
    index: int = 0
    #: The last populated row and column, zero-based. An unbounded range like
    #: A:A is clamped to these, not to the million rows a grid permits.
    max_row: int = 0
    max_column: int = 0
    range: str = ""
    merges: List[Merge] = field(default_factory=list)


@dataclass(frozen=True)
class Workbook:
    """A spreadsheet's structure: its tabs and what is on them."""

    document_id: str = ""
    sheets: List[Sheet] = field(default_factory=list)


@dataclass(frozen=True)
class DocumentState:
    """A document's change token: poll it, or send it as a precondition."""

    document_id: str = ""
    state: str = ""
    checked_at: str = ""


@dataclass(frozen=True)
class ValueRange:
    """One rectangle of cells.

    Reads are always rectangular — an empty cell arrives as an empty string —
    so a caller need not bounds-check each row. A cell is whatever fits: a
    string, a number, a bool, or a formula written as a string beginning "=".
    """

    range: str = ""
    values: List[List[Any]] = field(default_factory=list)


@dataclass(frozen=True)
class WriteResult:
    """What a value write reports."""

    #: Where the values landed. Set on a single-range write and on an append.
    range: str = ""
    updated_cells: int = 0
    #: The document's state after the write. Keep it to notice somebody else's
    #: edit, or to send as the precondition on a structural change.
    state: str = ""
    #: False when the write stored nothing new — writing a cell the value it
    #: already holds. Not an error and not worth retrying: nothing was stored
    #: and nobody with the document open was told.
    changed: bool = False


@dataclass(frozen=True)
class StructureResult:
    """What a structural batch reports.

    ``replies`` is positional: one entry per request, in the order sent, empty
    where an operation returns nothing.
    """

    replies: List[Dict[str, Any]] = field(default_factory=list)
    state: str = ""
    changed: bool = False

# ----------------------------------------------------- workspace and drive


@dataclass(frozen=True)
class Profile:
    """The user a key acts as. A key can never do more than they can."""

    id: str = ""
    email: str = ""
    name: str = ""
    role: str = ""
    workspace: Dict[str, Any] = field(default_factory=dict)
    #: Empty for an account with no mailbox.
    mailbox_address: str = ""
    avatar_url: str = ""


@dataclass(frozen=True)
class Group:
    """One workspace group as a member sees it."""

    id: str = ""
    name: str = ""
    description: str = ""
    #: None when withheld, which happens for a group the caller cannot see
    #: into. None is not zero: it means unknown, not empty.
    member_count: Optional[int] = None
    updated_at: str = ""


@dataclass(frozen=True)
class AddressBook:
    """One address book in the caller's mailbox."""

    id: str = ""
    name: str = ""
    description: str = ""
    contact_count: int = 0
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class ContactEmail:
    """One address on a contact."""

    address: str = ""
    type: str = ""


@dataclass(frozen=True)
class ContactPhone:
    """One number on a contact."""

    number: str = ""
    type: str = ""


@dataclass(frozen=True)
class Contact:
    """One entry in an address book."""

    id: str = ""
    address_book_id: str = ""
    name: str = ""
    given_name: str = ""
    family_name: str = ""
    emails: List[ContactEmail] = field(default_factory=list)
    phones: List[ContactPhone] = field(default_factory=list)
    organization: str = ""
    title: str = ""
    notes: str = ""
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class Document:
    """A document or a spreadsheet. An uploaded file is a :class:`File`."""

    id: str = ""
    type: str = ""
    title: str = ""
    #: "active" or "trashed". Deleting trashes; nothing here destroys.
    state: str = ""
    #: Who owns it, which is not necessarily the key's owner.
    owner_id: str = ""
    #: Empty for a document in the root.
    folder_id: str = ""
    #: Yours to shape. The API stores it and does not read it.
    metadata: Dict[str, Any] = field(default_factory=dict)
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class Comment:
    """One comment or reply on a document."""

    id: str = ""
    document_id: str = ""
    #: Set on a reply, empty on a thread's first comment.
    parent_id: str = ""
    author_id: str = ""
    body: str = ""
    #: "open" or "resolved".
    state: str = ""
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class Permission:
    """One grant of access to a document or file."""

    id: str = ""
    #: "user" or "group"; principal_id is that id, not an email.
    principal_type: str = ""
    principal_id: str = ""
    role: str = ""
    created_by: str = ""
    created_at: str = ""


@dataclass(frozen=True)
class File:
    """An uploaded file."""

    id: str = ""
    type: str = ""
    #: The display name, which can change.
    title: str = ""
    #: What it was uploaded as, which does not.
    filename: str = ""
    #: Sniffed from the bytes when stored, not taken from what was declared.
    mime_type: str = ""
    size_bytes: int = 0
    state: str = ""
    owner_id: str = ""
    folder_id: str = ""
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class FileFolder:
    """One folder in the drive."""

    id: str = ""
    name: str = ""
    #: Empty for a folder at the root.
    parent_id: str = ""
    owner_id: str = ""
    trashed: bool = False
    created_at: str = ""
    updated_at: str = ""


@dataclass(frozen=True)
class NewUpload:
    """A file about to be sent."""

    filename: str = ""
    #: Required: it decides the part size that comes back.
    size: int = 0
    #: Recorded, not trusted: the stored type is sniffed from the bytes.
    mime_type: str = ""
    folder_id: str = ""


@dataclass(frozen=True)
class UploadSession:
    """An upload in progress."""

    session_id: str = ""
    #: The file this will become, reserved before the bytes are all up.
    document_id: str = ""
    #: Slice by exactly this, every part but the last.
    part_size: int = 0
    total_size: int = 0
    status: str = ""
    #: Which numbers have landed. Read from storage, so a resume survives a
    #: client restart.
    uploaded_parts: List[int] = field(default_factory=list)
    uploaded_bytes: int = 0


@dataclass(frozen=True)
class UploadedPart:
    """The receipt for one part."""

    part: int = 0
    etag: str = ""


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
