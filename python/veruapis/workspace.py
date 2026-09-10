"""Who the key acts as, and the people around them.

Two small surfaces that answer the questions every integration asks first: who
am I acting as, and who else is here.
"""

from __future__ import annotations

from typing import Any, Dict, Iterator, List, Optional, Tuple

from ._client import path_escape
from .types import AddressBook, Contact, Group, Meta, Profile, decode, encode


class IdentityClient:
    """The key's own user, and the workspace directory."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def me(self) -> Profile:
        """The profile of the user this key acts as.

        The cheapest way to check a key works: one call, and it needs only
        ``identity.read``.
        """
        data, _ = self._api.request("GET", "/v1/me")
        return decode(Profile, data)

    def list_groups(
        self, *, limit: int = 0, cursor: str = ""
    ) -> Tuple[List[Group], Meta]:
        """One page of the workspace's groups, ordered by name.

        The directory behind a people picker, not the administrative view.
        """
        query: Dict[str, Any] = {}
        if limit:
            query["limit"] = limit
        if cursor:
            query["cursor"] = cursor

        data, meta = self._api.request("GET", "/v1/groups", query=query or None)
        return decode(List[Group], data), meta

    def groups(self) -> Iterator[Group]:
        """Every group, following the cursor."""
        for item in self._api.paginate("/v1/groups"):
            yield decode(Group, item)


class ContactsClient:
    """Address books and the people in them."""

    def __init__(self, api: Any) -> None:
        self._api = api

    def list_address_books(self) -> List[AddressBook]:
        """Every book in the caller's mailbox.

        Not paged: there are a handful, so paging would mean writing a loop
        that never runs twice.
        """
        data, _ = self._api.request("GET", "/v1/address-books")
        return decode(List[AddressBook], data)

    def list_contacts(
        self,
        *,
        address_book_id: str = "",
        q: str = "",
        limit: int = 0,
        cursor: str = "",
    ) -> Tuple[List[Contact], Meta]:
        """One page of contacts.

        ``q`` matches the name and every address on a contact, including
        secondary ones, so somebody's old address still finds them.
        """
        data, meta = self._api.request(
            "GET", "/v1/contacts", query=_contact_query(address_book_id, q, limit, cursor)
        )
        return decode(List[Contact], data), meta

    def contacts(
        self, *, address_book_id: str = "", q: str = ""
    ) -> Iterator[Contact]:
        """Every contact matching the filter, following the cursor."""
        query = _contact_query(address_book_id, q, 0, "")
        for item in self._api.paginate("/v1/contacts", query):
            yield decode(Contact, item)

    def get_contact(self, contact_id: str) -> Contact:
        """One contact."""
        data, _ = self._api.request("GET", "/v1/contacts/" + path_escape(contact_id))
        return decode(Contact, data)

    def create_contact(self, contact: Contact) -> Contact:
        """Add a contact to an address book.

        ``name`` is the only required field. A field this API does not publish
        is refused rather than ignored, so a typo is a 400 here rather than a
        value that silently never arrived.
        """
        data, _ = self._api.request("POST", "/v1/contacts", body=encode(contact))
        return decode(Contact, data)

    def update_contact(self, contact_id: str, contact: Contact) -> Contact:
        """Edit a contact.

        Fields left empty are left alone, but a list that is sent replaces the
        whole list rather than adding to it: read the contact first if you mean
        to append an address.
        """
        data, _ = self._api.request(
            "PATCH", "/v1/contacts/" + path_escape(contact_id), body=encode(contact)
        )
        return decode(Contact, data)

    def delete_contact(self, contact_id: str) -> None:
        """Remove a contact from its address book."""
        self._api.request("DELETE", "/v1/contacts/" + path_escape(contact_id))


def _contact_query(
    address_book_id: str, q: str, limit: int, cursor: str
) -> Optional[Dict[str, Any]]:
    query: Dict[str, Any] = {}
    if address_book_id:
        query["address_book_id"] = address_book_id
    if q:
        query["q"] = q
    if limit:
        query["limit"] = limit
    if cursor:
        query["cursor"] = cursor
    return query or None
