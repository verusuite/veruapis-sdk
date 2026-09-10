"""Documents and spreadsheets, their comments, and who they are shared with.

An uploaded file is not here. The API keeps the two apart — ``/v1/documents``
is what somebody authored, ``/v1/files`` is what somebody uploaded — so a
listing never begins with a filter, and asking the wrong collection for an id
answers 404 rather than something that half fits.
"""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Optional, Tuple

from ._client import path_escape
from .types import Comment, Document, Meta, Permission, decode, encode

#: The kinds of document that can be created. A file is not one of them:
#: uploading creates a file, and minting an empty one would leave a row with no
#: bytes behind it.
TYPE_DOCUMENT = "document"
TYPE_SPREADSHEET = "spreadsheet"


class DocumentsClient:
    """Documents, spreadsheets and their comments."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def list_documents(
        self,
        *,
        type: str = "",
        folder_id: str = "",
        q: str = "",
        trashed: bool = False,
        limit: int = 0,
        cursor: str = "",
    ) -> Tuple[List[Document], Meta]:
        """One page of documents and spreadsheets."""
        data, meta = self._api.request(
            "GET", "/v1/documents", query=_query(type, folder_id, q, trashed, limit, cursor)
        )
        return decode(List[Document], data), meta

    def documents(
        self,
        *,
        type: str = "",
        folder_id: str = "",
        q: str = "",
        trashed: bool = False,
    ) -> Iterator[Document]:
        """Every document matching the filter, following the cursor.

        This is how an automation finds the spreadsheet id every
        ``spreadsheets`` call needs, rather than having somebody paste one out
        of a browser.
        """
        query = _query(type, folder_id, q, trashed, 0, "")
        for item in self._api.paginate("/v1/documents", query):
            yield decode(Document, item)

    def get_document(self, document_id: str) -> Document:
        """One document's metadata.

        Metadata only, so this stays cheap on a large workbook. Contents are
        read through ``spreadsheets``.
        """
        data, _ = self._api.request("GET", "/v1/documents/" + path_escape(document_id))
        return decode(Document, data)

    def create_document(self, document: Document) -> Document:
        """Create a document or a spreadsheet.

        A new spreadsheet arrives with its workbook seeded, so a range can be
        written into it in the very next call.
        """
        data, _ = self._api.request("POST", "/v1/documents", body=encode(document))
        return decode(Document, data)

    def update_document(self, document_id: str, document: Document) -> Document:
        """Rename a document or change its metadata.

        Anyone with it open is told, so a title changed here appears in their
        tab without a reload. ``metadata`` replaces the stored object rather
        than merging into it.
        """
        data, _ = self._api.request(
            "PATCH", "/v1/documents/" + path_escape(document_id), body=encode(document)
        )
        return decode(Document, data)

    def delete_document(self, document_id: str) -> None:
        """Move a document to the trash.

        Reversible with :meth:`restore_document`. A permanent delete is not
        exposed at all, so a job retrying a failed batch cannot destroy
        somebody's work.
        """
        self._api.request("DELETE", "/v1/documents/" + path_escape(document_id))

    def restore_document(self, document_id: str) -> Document:
        """Take a document back out of the trash.

        Restoring one that was never trashed changes nothing and still answers
        with it, so a retry is safe.
        """
        data, _ = self._api.request(
            "POST", "/v1/documents/" + path_escape(document_id) + "/restore"
        )
        return decode(Document, data)

    def copy_document(
        self, document_id: str, *, title: str = "", folder_id: str = ""
    ) -> Document:
        """Copy a document with its contents.

        Both arguments are optional: with neither, the copy lands in the root
        as "Copy of" the original. The copy belongs to the key's owner.
        """
        body: Dict[str, Any] = {}
        if title:
            body["title"] = title
        if folder_id:
            body["folder_id"] = folder_id

        data, _ = self._api.request(
            "POST", "/v1/documents/" + path_escape(document_id) + "/copy", body=body
        )
        return decode(Document, data)

    def list_comments(
        self, document_id: str, *, include_resolved: bool = True
    ) -> List[Comment]:
        """The comment threads on a document.

        Resolved threads are included unless you say otherwise, which is the
        opposite of the editor's sidebar: an integration auditing a document
        wants the whole history.
        """
        query = None if include_resolved else {"include_resolved": "false"}
        data, _ = self._api.request(
            "GET", "/v1/documents/" + path_escape(document_id) + "/comments", query=query
        )
        return decode(List[Comment], data)

    def create_comment(self, document_id: str, comment: Comment) -> Comment:
        """Post a comment, or a reply when ``parent_id`` is set."""
        data, _ = self._api.request(
            "POST",
            "/v1/documents/" + path_escape(document_id) + "/comments",
            body=encode(comment),
        )
        return decode(Comment, data)

    def update_comment(self, comment_id: str, comment: Comment) -> Comment:
        """Edit a comment's text or resolve the thread.

        The two are not the same permission: anyone who may comment can resolve
        or reopen a thread, while editing the words is the author's alone.
        """
        data, _ = self._api.request(
            "PATCH", "/v1/comments/" + path_escape(comment_id), body=encode(comment)
        )
        return decode(Comment, data)

    def delete_comment(self, comment_id: str) -> None:
        """Remove a comment. Only its author may."""
        self._api.request("DELETE", "/v1/comments/" + path_escape(comment_id))


def _query(
    type: str, folder_id: str, q: str, trashed: bool, limit: int, cursor: str
) -> Optional[Dict[str, Any]]:
    query: Dict[str, Any] = {}
    if type:
        query["type"] = type
    if folder_id:
        query["folder_id"] = folder_id
    if q:
        query["q"] = q
    if trashed:
        query["trashed"] = "true"
    if limit:
        query["limit"] = limit
    if cursor:
        query["cursor"] = cursor
    return query or None
