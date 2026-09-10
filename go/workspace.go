package veruapis

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
)

// IdentityService is who the key acts as, and the people around them.
type IdentityService struct{ client *Client }

// ContactsService is address books and the people in them.
type ContactsService struct{ client *Client }

// Profile is the user a key acts as.
//
// Smaller than it looks like it should be, deliberately: a key can never do
// more than the person who created it, so the useful question is who that is,
// not what the workspace holds.
type Profile struct {
	ID    string `json:"id"`
	Email string `json:"email"`
	Name  string `json:"name"`
	Role  string `json:"role"`

	Workspace struct {
		ID string `json:"id"`
	} `json:"workspace"`

	// MailboxAddress is empty for an account with no mailbox.
	MailboxAddress string `json:"mailbox_address,omitempty"`
	AvatarURL      string `json:"avatar_url,omitempty"`
}

// Group is one workspace group as a member sees it.
type Group struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description,omitempty"`

	// MemberCount is nil when withheld, which happens for a group the caller
	// cannot see into. Nil is not zero: it means unknown, not empty, and
	// reporting "this team has nobody in it" from a withheld count is the
	// mistake this pointer exists to prevent.
	MemberCount *int   `json:"member_count"`
	UpdatedAt   string `json:"updated_at,omitempty"`
}

// AddressBook is one book in the caller's mailbox.
type AddressBook struct {
	ID           string `json:"id"`
	Name         string `json:"name"`
	Description  string `json:"description,omitempty"`
	ContactCount int    `json:"contact_count"`
	CreatedAt    string `json:"created_at,omitempty"`
	UpdatedAt    string `json:"updated_at,omitempty"`
}

// ContactEmail is one address on a contact.
type ContactEmail struct {
	Address string `json:"address"`
	Type    string `json:"type,omitempty"`
}

// ContactPhone is one number on a contact.
type ContactPhone struct {
	Number string `json:"number"`
	Type   string `json:"type,omitempty"`
}

// Contact is one entry in an address book.
type Contact struct {
	ID            string         `json:"id,omitempty"`
	AddressBookID string         `json:"address_book_id,omitempty"`
	Name          string         `json:"name,omitempty"`
	GivenName     string         `json:"given_name,omitempty"`
	FamilyName    string         `json:"family_name,omitempty"`
	Emails        []ContactEmail `json:"emails,omitempty"`
	Phones        []ContactPhone `json:"phones,omitempty"`
	Organization  string         `json:"organization,omitempty"`
	Title         string         `json:"title,omitempty"`
	Notes         string         `json:"notes,omitempty"`
	CreatedAt     string         `json:"created_at,omitempty"`
	UpdatedAt     string         `json:"updated_at,omitempty"`
}

// ListContactsOptions filters a contact listing.
type ListContactsOptions struct {
	AddressBookID string

	// Search matches the name and every address on a contact, including
	// secondary ones, so an old address still finds the person.
	Search string
	Limit  int
	Cursor string
}

func (o ListContactsOptions) query() url.Values {
	q := url.Values{}
	if o.AddressBookID != "" {
		q.Set("address_book_id", o.AddressBookID)
	}
	if o.Search != "" {
		q.Set("q", o.Search)
	}
	if o.Limit > 0 {
		q.Set("limit", strconv.Itoa(o.Limit))
	}
	if o.Cursor != "" {
		q.Set("cursor", o.Cursor)
	}
	return q
}

// Me returns the profile of the user this key acts as.
//
// The cheapest way to check a key works: one call, and it needs only
// identity.read.
func (s *IdentityService) Me(ctx context.Context) (Profile, error) {
	out, _, err := Do[Profile](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/me",
	})
	return out, err
}

// ListGroups returns one page of the workspace's groups, ordered by name.
//
// The directory behind a people picker, not the administrative view.
func (s *IdentityService) ListGroups(ctx context.Context, limit int, cursor string) ([]Group, Meta, error) {
	q := url.Values{}
	if limit > 0 {
		q.Set("limit", strconv.Itoa(limit))
	}
	if cursor != "" {
		q.Set("cursor", cursor)
	}
	return Do[[]Group](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/groups",
		Query:  q,
	})
}

// Groups iterates every group, following the cursor.
func (s *IdentityService) Groups(ctx context.Context) Seq2[Group] {
	return paginate[Group](ctx, s.client, "/v1/groups", nil)
}

// ListAddressBooks returns every book in the caller's mailbox.
//
// Not paged: there are a handful, so paging would mean writing a loop that
// never runs twice.
func (s *ContactsService) ListAddressBooks(ctx context.Context) ([]AddressBook, error) {
	out, _, err := Do[[]AddressBook](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/address-books",
	})
	return out, err
}

// ListContacts returns one page of contacts.
func (s *ContactsService) ListContacts(ctx context.Context, opts ListContactsOptions) ([]Contact, Meta, error) {
	return Do[[]Contact](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/contacts",
		Query:  opts.query(),
	})
}

// Contacts iterates every contact matching the filter, following the cursor.
func (s *ContactsService) Contacts(ctx context.Context, opts ListContactsOptions) Seq2[Contact] {
	return paginate[Contact](ctx, s.client, "/v1/contacts", opts.query())
}

// GetContact returns one contact.
func (s *ContactsService) GetContact(ctx context.Context, id string) (Contact, error) {
	out, _, err := Do[Contact](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/contacts/" + url.PathEscape(id),
	})
	return out, err
}

// CreateContact adds a contact to an address book.
//
// Name is the only field required. A field this API does not publish is
// refused rather than ignored, so a typo is a 400 here rather than a value
// that silently never arrived.
func (s *ContactsService) CreateContact(ctx context.Context, contact Contact) (Contact, error) {
	out, _, err := Do[Contact](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/contacts",
		Body:   contact,
	})
	return out, err
}

// UpdateContact edits a contact.
//
// Fields left empty are left alone, but a list that is sent replaces the whole
// list rather than adding to it: read the contact first if you mean to append
// an address.
func (s *ContactsService) UpdateContact(ctx context.Context, id string, contact Contact) (Contact, error) {
	out, _, err := Do[Contact](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/contacts/" + url.PathEscape(id),
		Body:   contact,
	})
	return out, err
}

// DeleteContact removes a contact from its address book.
func (s *ContactsService) DeleteContact(ctx context.Context, id string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/contacts/" + url.PathEscape(id),
	})
	return err
}
