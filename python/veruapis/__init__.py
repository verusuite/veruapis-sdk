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
    Attachment,
    Attendee,
    BusyPeriod,
    Calendar,
    Event,
    Folder,
    FreeBusy,
    Message,
    MessageFlags,
    MessageSummary,
    Meta,
    OutgoingAttachment,
    SendMessage,
    Sent,
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
    "__version__",
]
