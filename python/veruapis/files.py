"""Uploaded files, their folders, sharing, and the resumable upload protocol.

The folders are file-folders rather than folders because ``/v1/folders`` is
mail's. Two products, one word, and the API spells the less obvious one out
rather than leaving a caller to guess which listing they are reading.
"""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Optional, Tuple

from ._client import path_escape
from .types import (
    File,
    FileFolder,
    Meta,
    NewUpload,
    Permission,
    UploadedPart,
    UploadSession,
    decode,
    encode,
)


class FilesClient:
    """Files, folders, sharing and uploads."""

    def __init__(self, api: Any) -> None:
        self._api = api

    # ------------------------------------------------------------- folders

    def list_folders(self, *, trashed: bool = False) -> List[FileFolder]:
        """The drive's folders, or its trash.

        A flat list with parent ids rather than a nested tree, so a caller
        builds whichever shape it needs rather than this API deciding the
        depth.
        """
        query = {"trashed": "true"} if trashed else None
        data, _ = self._api.request("GET", "/v1/file-folders", query=query)
        return decode(List[FileFolder], data)

    def create_folder(self, folder: FileFolder) -> FileFolder:
        """Make a folder, at the root unless ``parent_id`` says otherwise."""
        data, _ = self._api.request("POST", "/v1/file-folders", body=encode(folder))
        return decode(FileFolder, data)

    def update_folder(
        self,
        folder_id: str,
        *,
        name: str = "",
        parent_id: str = "",
        move_to_root: bool = False,
    ) -> FileFolder:
        """Rename a folder, move it, or both in that order.

        ``move_to_root`` is separate from ``parent_id`` because an empty parent
        and an absent one are the same thing once they have been through JSON,
        and "leave it where it is" and "move it to the top" are not the same
        instruction.
        """
        body: Dict[str, Any] = {}
        if name:
            body["name"] = name
        if move_to_root:
            body["parent_id"] = ""
        elif parent_id:
            body["parent_id"] = parent_id

        data, _ = self._api.request(
            "PATCH", "/v1/file-folders/" + path_escape(folder_id), body=body
        )
        return decode(FileFolder, data)

    def delete_folder(self, folder_id: str) -> None:
        """Move a folder to the trash."""
        self._api.request("DELETE", "/v1/file-folders/" + path_escape(folder_id))

    # --------------------------------------------------------------- files

    def list_files(
        self,
        *,
        folder_id: str = "",
        q: str = "",
        trashed: bool = False,
        limit: int = 0,
        cursor: str = "",
    ) -> Tuple[List[File], Meta]:
        """One page of uploaded files."""
        data, meta = self._api.request(
            "GET", "/v1/files", query=_query(folder_id, q, trashed, limit, cursor)
        )
        return decode(List[File], data), meta

    def files(
        self, *, folder_id: str = "", q: str = "", trashed: bool = False
    ) -> Iterator[File]:
        """Every file matching the filter, following the cursor."""
        for item in self._api.paginate("/v1/files", _query(folder_id, q, trashed, 0, "")):
            yield decode(File, item)

    def get_file(self, file_id: str) -> File:
        """One file's metadata."""
        data, _ = self._api.request("GET", "/v1/files/" + path_escape(file_id))
        return decode(File, data)

    def download(self, file_id: str, *, range: str = "") -> Tuple[bytes, str]:
        """A file's bytes and its content type.

        ``range`` is an HTTP range such as ``bytes=0-1048575``, or empty for the
        whole file. Worth using for anything large: the response is proxied and
        bounded, so a big file is fetched in parts rather than in one call that
        cannot finish.
        """
        return self._api.download(
            "/v1/files/" + path_escape(file_id) + "/content", range=range or None
        )

    def update_file(
        self,
        file_id: str,
        *,
        name: str = "",
        folder_id: str = "",
        move_to_root: bool = False,
    ) -> File:
        """Rename a file, move it, or both."""
        body: Dict[str, Any] = {}
        if name:
            body["name"] = name
        if move_to_root:
            body["folder_id"] = ""
        elif folder_id:
            body["folder_id"] = folder_id

        data, _ = self._api.request("PATCH", "/v1/files/" + path_escape(file_id), body=body)
        return decode(File, data)

    def delete_file(self, file_id: str) -> None:
        """Move a file to the trash. Nothing here frees the bytes."""
        self._api.request("DELETE", "/v1/files/" + path_escape(file_id))

    # ------------------------------------------------------------- uploads

    def start_upload(self, upload: NewUpload) -> UploadSession:
        """Open a resumable session and learn the part size to slice by.

        The way to upload anything large: a single request is capped at the
        edge and cut at 100 seconds, so past a certain size one call cannot
        succeed however patient the caller is.
        """
        data, _ = self._api.request("POST", "/v1/files/uploads", body=encode(upload))
        return decode(UploadSession, data)

    def upload_part(self, session_id: str, part: int, chunk: bytes) -> UploadedPart:
        """Send one part's bytes.

        Parts count from 1, and re-sending a number overwrites it: a part whose
        response was lost is simply sent again rather than restarting the
        upload.
        """
        data, _ = self._api.request(
            "PUT",
            "/v1/files/uploads/" + path_escape(session_id) + "/parts/" + str(part),
            raw_body=chunk,
            content_type="application/octet-stream",
        )
        return decode(UploadedPart, data)

    def upload_status(self, session_id: str) -> UploadSession:
        """Which parts have landed.

        The point of a resumable upload: after an interruption, send only what
        is missing. A read, so it needs ``files.read`` where the rest of the
        session needs ``files.write``.
        """
        data, _ = self._api.request("GET", "/v1/files/uploads/" + path_escape(session_id))
        return decode(UploadSession, data)

    def complete_upload(self, session_id: str) -> File:
        """Assemble the parts into a file."""
        data, _ = self._api.request(
            "POST", "/v1/files/uploads/" + path_escape(session_id) + "/complete"
        )
        return decode(File, data)

    def abort_upload(self, session_id: str) -> None:
        """Abandon a session and discard its parts.

        Worth calling when you give up: the parts already stored count against
        the workspace until the session is abandoned.
        """
        self._api.request("DELETE", "/v1/files/uploads/" + path_escape(session_id))

    # ------------------------------------------------------------- sharing

    def list_permissions(self, file_id: str) -> List[Permission]:
        """Who a file or document is shared with.

        Needs manage access on the thing itself, not only the scope: who
        something is shared with is not something a viewer may read.
        """
        data, _ = self._api.request("GET", "/v1/files/" + path_escape(file_id) + "/permissions")
        return decode(List[Permission], data)

    def share(self, file_id: str, permission: Permission) -> Permission:
        """Grant somebody access.

        A person is named by their workspace user id rather than their email.
        Needs ``files.share``, which is separate from ``files.write`` on
        purpose: organising a drive and exposing it are different risks.
        """
        data, _ = self._api.request(
            "POST",
            "/v1/files/" + path_escape(file_id) + "/permissions",
            body=encode(permission),
        )
        return decode(Permission, data)

    def unshare(self, file_id: str, permission_id: str) -> None:
        """Revoke one grant.

        Takes the permission's id from the listing, not the person's: one
        person can hold access through more than one grant.
        """
        self._api.request(
            "DELETE",
            "/v1/files/"
            + path_escape(file_id)
            + "/permissions/"
            + path_escape(permission_id),
        )


def _query(
    folder_id: str, q: str, trashed: bool, limit: int, cursor: str
) -> Optional[Dict[str, Any]]:
    query: Dict[str, Any] = {}
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
