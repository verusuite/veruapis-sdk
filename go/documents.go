package veruapis

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
)

// DocumentsService is documents and spreadsheets, their comments, and who they
// are shared with.
//
// An uploaded file is not here. The API keeps the two apart — /v1/documents is
// what somebody authored, /v1/files is what somebody uploaded — so a listing
// never begins with a filter, and asking the wrong collection for an id answers
// 404 rather than something that half fits.
type DocumentsService struct{ client *Client }

// Document is a document or a spreadsheet.
type Document struct {
	ID    string `json:"id,omitempty"`
	Type  string `json:"type,omitempty"`
	Title string `json:"title,omitempty"`

	// State is "active" or "trashed". Deleting moves a document to the trash
	// and Restore brings it back; nothing on this API destroys one.
	State string `json:"state,omitempty"`

	// OwnerID is who owns it, which is not necessarily the key's owner.
	OwnerID string `json:"owner_id,omitempty"`

	// FolderID is empty for a document in the root.
	FolderID string `json:"folder_id,omitempty"`

	// Metadata is yours to shape. The API stores it and does not read it.
	Metadata  map[string]any `json:"metadata,omitempty"`
	CreatedAt string         `json:"created_at,omitempty"`
	UpdatedAt string         `json:"updated_at,omitempty"`
}

// The kinds of document that can be created. A file is not one of them:
// uploading creates a file, and minting an empty one would leave a row with no
// bytes behind it.
const (
	TypeDocument    = "document"
	TypeSpreadsheet = "spreadsheet"
)

// Comment is one comment or reply on a document.
type Comment struct {
	ID         string `json:"id,omitempty"`
	DocumentID string `json:"document_id,omitempty"`

	// ParentID makes a comment a reply. Empty on a thread's first comment.
	ParentID string `json:"parent_id,omitempty"`
	AuthorID string `json:"author_id,omitempty"`
	Body     string `json:"body,omitempty"`

	// State is "open" or "resolved".
	State     string `json:"state,omitempty"`
	CreatedAt string `json:"created_at,omitempty"`
	UpdatedAt string `json:"updated_at,omitempty"`
}

// Permission is one grant of access to a document or file.
type Permission struct {
	ID string `json:"id,omitempty"`

	// PrincipalType is "user" or "group"; PrincipalID is that id, not an email.
	PrincipalType string `json:"principal_type,omitempty"`
	PrincipalID   string `json:"principal_id,omitempty"`
	Role          string `json:"role,omitempty"`
	CreatedBy     string `json:"created_by,omitempty"`
	CreatedAt     string `json:"created_at,omitempty"`
}

// ListDocumentsOptions filters a document listing.
type ListDocumentsOptions struct {
	// Type is TypeDocument or TypeSpreadsheet. Empty returns both.
	Type     string
	FolderID string
	Search   string

	// Trashed lists the trash instead of the drive.
	Trashed bool
	Limit   int
	Cursor  string
}

func (o ListDocumentsOptions) query() url.Values {
	q := url.Values{}
	if o.Type != "" {
		q.Set("type", o.Type)
	}
	if o.FolderID != "" {
		q.Set("folder_id", o.FolderID)
	}
	if o.Search != "" {
		q.Set("q", o.Search)
	}
	if o.Trashed {
		q.Set("trashed", "true")
	}
	if o.Limit > 0 {
		q.Set("limit", strconv.Itoa(o.Limit))
	}
	if o.Cursor != "" {
		q.Set("cursor", o.Cursor)
	}
	return q
}

// ListDocuments returns one page of documents and spreadsheets.
func (s *DocumentsService) ListDocuments(ctx context.Context, opts ListDocumentsOptions) ([]Document, Meta, error) {
	return Do[[]Document](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/documents",
		Query:  opts.query(),
	})
}

// Documents iterates every document matching the filter, following the cursor.
//
// This is how an automation finds the spreadsheet id every Spreadsheets call
// needs, rather than having somebody paste one out of a browser.
func (s *DocumentsService) Documents(ctx context.Context, opts ListDocumentsOptions) Seq2[Document] {
	return paginate[Document](ctx, s.client, "/v1/documents", opts.query())
}

// GetDocument returns one document's metadata.
//
// Metadata only, so this stays cheap on a large workbook. Contents are read
// through Spreadsheets.
func (s *DocumentsService) GetDocument(ctx context.Context, id string) (Document, error) {
	out, _, err := Do[Document](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/documents/" + url.PathEscape(id),
	})
	return out, err
}

// CreateDocument creates a document or a spreadsheet.
//
// A new spreadsheet arrives with its workbook seeded, so a range can be written
// into it in the very next call.
func (s *DocumentsService) CreateDocument(ctx context.Context, doc Document) (Document, error) {
	out, _, err := Do[Document](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/documents",
		Body:   doc,
	})
	return out, err
}

// UpdateDocument renames a document or changes its metadata.
//
// Anyone with it open is told, so a title changed here appears in their tab
// without a reload. Metadata replaces the stored object rather than merging
// into it.
func (s *DocumentsService) UpdateDocument(ctx context.Context, id string, doc Document) (Document, error) {
	out, _, err := Do[Document](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/documents/" + url.PathEscape(id),
		Body:   doc,
	})
	return out, err
}

// DeleteDocument moves a document to the trash.
//
// Reversible with RestoreDocument. A permanent delete is not exposed at all, so
// a job retrying a failed batch cannot destroy somebody's work.
func (s *DocumentsService) DeleteDocument(ctx context.Context, id string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/documents/" + url.PathEscape(id),
	})
	return err
}

// RestoreDocument takes a document back out of the trash.
//
// Restoring one that was never trashed changes nothing and still answers with
// it, so a retry is safe.
func (s *DocumentsService) RestoreDocument(ctx context.Context, id string) (Document, error) {
	out, _, err := Do[Document](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/documents/" + url.PathEscape(id) + "/restore",
	})
	return out, err
}

// CopyDocument copies a document with its contents.
//
// Both fields on the copy are optional: an empty Document copies into the root
// as "Copy of" the original. Viewing the source is enough, because nothing
// about it changes; the copy belongs to the key's owner.
func (s *DocumentsService) CopyDocument(ctx context.Context, id string, copy Document) (Document, error) {
	out, _, err := Do[Document](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/documents/" + url.PathEscape(id) + "/copy",
		Body:   copy,
	})
	return out, err
}

// ListComments returns the comment threads on a document.
//
// includeResolved is true by default in the API, which is the opposite of the
// editor's sidebar: an integration auditing a document wants the whole history
// rather than the open half.
func (s *DocumentsService) ListComments(ctx context.Context, documentID string, includeResolved bool) ([]Comment, error) {
	q := url.Values{}
	if !includeResolved {
		q.Set("include_resolved", "false")
	}
	out, _, err := Do[[]Comment](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/documents/" + url.PathEscape(documentID) + "/comments",
		Query:  q,
	})
	return out, err
}

// CreateComment posts a comment, or a reply when ParentID is set.
func (s *DocumentsService) CreateComment(ctx context.Context, documentID string, comment Comment) (Comment, error) {
	out, _, err := Do[Comment](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/documents/" + url.PathEscape(documentID) + "/comments",
		Body:   comment,
	})
	return out, err
}

// UpdateComment edits a comment's text or resolves the thread.
//
// The two are not the same permission: anyone who may comment can resolve or
// reopen a thread, while editing the words is the author's alone.
func (s *DocumentsService) UpdateComment(ctx context.Context, commentID string, comment Comment) (Comment, error) {
	out, _, err := Do[Comment](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/comments/" + url.PathEscape(commentID),
		Body:   comment,
	})
	return out, err
}

// DeleteComment removes a comment. Only its author may.
func (s *DocumentsService) DeleteComment(ctx context.Context, commentID string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/comments/" + url.PathEscape(commentID),
	})
	return err
}
