"""Client for the VeruSuite API: mail, calendar and workspace administration.

    from veruapis import VeruApi, VeruApiError

    api = VeruApi(api_key=os.environ["VERUAPIS_KEY"])

    for folder in api.mail.list_folders():
        print(folder.name, folder.unread_count)

Written by hand, types included, and checked against the API's own description
by a test rather than generated from it.
"""

from ._client import DEFAULT_BASE_URL, VeruApi
from .errors import ErrorDetail, VeruApiError
from .types import (
    Address,
    AddressBook,
    Attachment,
    Attendee,
    BusyPeriod,
    Calendar,
    Comment,
    Contact,
    ContactEmail,
    ContactPhone,
    Document,
    DocumentState,
    File,
    FileFolder,
    Group,
    NewUpload,
    Permission,
    Profile,
    UploadedPart,
    UploadSession,
    Event,
    Folder,
    FreeBusy,
    Merge,
    Message,
    MessageFlags,
    MessageSummary,
    Meta,
    OutgoingAttachment,
    SendMessage,
    Sent,
    Sheet,
    StructureResult,
    ValueRange,
    Workbook,
    WriteResult,
)

__version__ = "1.0.0"

__all__ = [
    "DEFAULT_BASE_URL",
    "VeruApi",
    "VeruApiError",
    "ErrorDetail",
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
    "AddressBook",
    "Comment",
    "Contact",
    "ContactEmail",
    "ContactPhone",
    "Document",
    "File",
    "FileFolder",
    "Group",
    "NewUpload",
    "Permission",
    "Profile",
    "UploadSession",
    "UploadedPart",
    "Sheet",
    "StructureResult",
    "ValueRange",
    "Workbook",
    "WriteResult",
    "DocumentState",
    "Merge",
    "__version__",
]
