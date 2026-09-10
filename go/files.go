package veruapis

import (
	"context"
	"net/http"
	"net/url"
	"strconv"
)

// FilesService is uploaded files, their folders, sharing, and the resumable
// upload protocol.
//
// A file's folders are file-folders rather than folders because /v1/folders is
// mail's. Two products, one word, and the API chose to spell the less obvious
// one out rather than have a caller guess which listing they were reading.
type FilesService struct{ client *Client }

// File is an uploaded file.
//
// Title is the display name and can be changed; Filename is what it was
// uploaded as and does not.
type File struct {
	ID       string `json:"id,omitempty"`
	Type     string `json:"type,omitempty"`
	Title    string `json:"title,omitempty"`
	Filename string `json:"filename,omitempty"`

	// MimeType is sniffed from the bytes when they were stored, not taken from
	// what the uploader declared.
	MimeType  string `json:"mime_type,omitempty"`
	SizeBytes *int64 `json:"size_bytes,omitempty"`
	State     string `json:"state,omitempty"`
	OwnerID   string `json:"owner_id,omitempty"`
	FolderID  string `json:"folder_id,omitempty"`
	CreatedAt string `json:"created_at,omitempty"`
	UpdatedAt string `json:"updated_at,omitempty"`
}

// Folder is one folder in the drive.
type FileFolder struct {
	ID   string `json:"id,omitempty"`
	Name string `json:"name,omitempty"`

	// ParentID is empty for a folder at the root.
	ParentID  string `json:"parent_id,omitempty"`
	OwnerID   string `json:"owner_id,omitempty"`
	Trashed   bool   `json:"trashed,omitempty"`
	CreatedAt string `json:"created_at,omitempty"`
	UpdatedAt string `json:"updated_at,omitempty"`
}

// UploadSession is an upload in progress.
type UploadSession struct {
	SessionID string `json:"session_id"`

	// DocumentID is the file this will become, reserved at the start so it can
	// be recorded before the bytes are all up.
	DocumentID string `json:"document_id"`

	// PartSize is what to slice by, every part but the last. Answered rather
	// than chosen, so it can change without breaking a client.
	PartSize  int64 `json:"part_size"`
	TotalSize int64 `json:"total_size"`

	Status string `json:"status,omitempty"`

	// UploadedParts is which numbers have landed, read from storage rather
	// than from anything a client remembered — so a resume survives a restart.
	UploadedParts []int  `json:"uploaded_parts,omitempty"`
	UploadedBytes *int64 `json:"uploaded_bytes,omitempty"`
}

// UploadedPart is the receipt for one part.
type UploadedPart struct {
	Part int    `json:"part"`
	ETag string `json:"etag"`
}

// NewUpload describes a file about to be sent.
type NewUpload struct {
	Filename string `json:"filename"`

	// Size is required: it decides the part size that comes back.
	Size int64 `json:"size"`

	// MimeType is recorded, not trusted. The stored type is sniffed from the
	// bytes, which is what stops an executable arriving labelled as an image.
	MimeType string `json:"mime_type,omitempty"`
	FolderID string `json:"folder_id,omitempty"`
}

// ListFilesOptions filters a file listing.
type ListFilesOptions struct {
	FolderID string
	Search   string
	Trashed  bool
	Limit    int
	Cursor   string
}

func (o ListFilesOptions) query() url.Values {
	q := url.Values{}
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

// ListFolders returns the drive's folders, or its trash.
//
// A flat list with parent ids rather than a nested tree, so a caller builds
// whichever shape it needs rather than this API deciding the depth.
func (s *FilesService) ListFolders(ctx context.Context, trashed bool) ([]FileFolder, error) {
	q := url.Values{}
	if trashed {
		q.Set("trashed", "true")
	}
	out, _, err := Do[[]FileFolder](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/file-folders",
		Query:  q,
	})
	return out, err
}

// CreateFolder makes a folder, at the root unless ParentID says otherwise.
func (s *FilesService) CreateFolder(ctx context.Context, folder FileFolder) (FileFolder, error) {
	out, _, err := Do[FileFolder](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/file-folders",
		Body:   folder,
	})
	return out, err
}

// UpdateFolder renames a folder, moves it, or both in that order.
//
// moveToRoot is separate from ParentID because an empty parent and an absent
// one are the same thing once they have been through JSON, and "leave it where
// it is" and "move it to the top" are not the same instruction.
func (s *FilesService) UpdateFolder(ctx context.Context, id, name string, parentID string, moveToRoot bool) (FileFolder, error) {
	body := map[string]any{}
	if name != "" {
		body["name"] = name
	}
	switch {
	case moveToRoot:
		body["parent_id"] = ""
	case parentID != "":
		body["parent_id"] = parentID
	}

	out, _, err := Do[FileFolder](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/file-folders/" + url.PathEscape(id),
		Body:   body,
	})
	return out, err
}

// DeleteFolder moves a folder to the trash.
func (s *FilesService) DeleteFolder(ctx context.Context, id string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/file-folders/" + url.PathEscape(id),
	})
	return err
}

// ListFiles returns one page of uploaded files.
func (s *FilesService) ListFiles(ctx context.Context, opts ListFilesOptions) ([]File, Meta, error) {
	return Do[[]File](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/files",
		Query:  opts.query(),
	})
}

// Files iterates every file matching the filter, following the cursor.
func (s *FilesService) Files(ctx context.Context, opts ListFilesOptions) Seq2[File] {
	return paginate[File](ctx, s.client, "/v1/files", opts.query())
}

// GetFile returns one file's metadata.
func (s *FilesService) GetFile(ctx context.Context, id string) (File, error) {
	out, _, err := Do[File](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/files/" + url.PathEscape(id),
	})
	return out, err
}

// Download returns a file's bytes and its content type.
//
// rangeHeader is an HTTP Range such as "bytes=0-1048575", or empty for the
// whole file. Worth using for anything large: the response is proxied and
// bounded, so a big file is fetched in parts rather than in one call that
// cannot finish.
func (s *FilesService) Download(ctx context.Context, id, rangeHeader string) ([]byte, string, error) {
	return Bytes(ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/files/" + url.PathEscape(id) + "/content",
		Range:  rangeHeader,
	})
}

// UpdateFile renames a file, moves it, or both.
//
// An empty name leaves it alone; moveToRoot moves it out of every folder, for
// the reason UpdateFolder gives.
func (s *FilesService) UpdateFile(ctx context.Context, id, name, folderID string, moveToRoot bool) (File, error) {
	body := map[string]any{}
	if name != "" {
		body["name"] = name
	}
	switch {
	case moveToRoot:
		body["folder_id"] = ""
	case folderID != "":
		body["folder_id"] = folderID
	}

	out, _, err := Do[File](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/files/" + url.PathEscape(id),
		Body:   body,
	})
	return out, err
}

// DeleteFile moves a file to the trash. Nothing here frees the bytes.
func (s *FilesService) DeleteFile(ctx context.Context, id string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/files/" + url.PathEscape(id),
	})
	return err
}

// StartUpload opens a resumable session and answers the part size to slice by.
//
// The way to upload anything large: a single request is capped at the edge and
// cut at 100 seconds, so past a certain size one call cannot succeed however
// patient the caller is.
func (s *FilesService) StartUpload(ctx context.Context, upload NewUpload) (UploadSession, error) {
	out, _, err := Do[UploadSession](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/files/uploads",
		Body:   upload,
	})
	return out, err
}

// UploadPart sends one part's bytes.
//
// Parts count from 1, and re-sending a number overwrites it: a part whose
// response was lost is simply sent again rather than restarting the upload.
func (s *FilesService) UploadPart(ctx context.Context, sessionID string, part int, body []byte) (UploadedPart, error) {
	out, _, err := Do[UploadedPart](ctx, s.client, Request{
		Method:      http.MethodPut,
		Path:        "/v1/files/uploads/" + url.PathEscape(sessionID) + "/parts/" + strconv.Itoa(part),
		RawBody:     body,
		ContentType: "application/octet-stream",
	})
	return out, err
}

// UploadStatus reports which parts have landed.
//
// The point of a resumable upload: after an interruption, send only what is
// missing. A read, so it needs files.read where the rest of the session needs
// files.write.
func (s *FilesService) UploadStatus(ctx context.Context, sessionID string) (UploadSession, error) {
	out, _, err := Do[UploadSession](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/files/uploads/" + url.PathEscape(sessionID),
	})
	return out, err
}

// CompleteUpload assembles the parts into a file.
func (s *FilesService) CompleteUpload(ctx context.Context, sessionID string) (File, error) {
	out, _, err := Do[File](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/files/uploads/" + url.PathEscape(sessionID) + "/complete",
	})
	return out, err
}

// AbortUpload abandons a session and discards its parts.
//
// Worth calling when you give up: the parts already stored count against the
// workspace until the session is abandoned.
func (s *FilesService) AbortUpload(ctx context.Context, sessionID string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/files/uploads/" + url.PathEscape(sessionID),
	})
	return err
}

// ListPermissions reads who a file or document is shared with.
//
// Needs manage access on the thing itself, not only the scope: who something is
// shared with is not something a viewer may read.
func (s *FilesService) ListPermissions(ctx context.Context, id string) ([]Permission, error) {
	out, _, err := Do[[]Permission](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/files/" + url.PathEscape(id) + "/permissions",
	})
	return out, err
}

// Share grants somebody access.
//
// A person is named by their workspace user id rather than their email —
// Identity.ListGroups and the directory resolve one. Needs files.share, which
// is separate from files.write on purpose: organising a drive and exposing it
// are different risks.
func (s *FilesService) Share(ctx context.Context, id string, permission Permission) (Permission, error) {
	out, _, err := Do[Permission](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/files/" + url.PathEscape(id) + "/permissions",
		Body:   permission,
	})
	return out, err
}

// Unshare revokes one grant.
//
// Takes the permission's id from the listing, not the person's: one person can
// hold access through more than one grant.
func (s *FilesService) Unshare(ctx context.Context, id, permissionID string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/files/" + url.PathEscape(id) + "/permissions/" + url.PathEscape(permissionID),
	})
	return err
}
