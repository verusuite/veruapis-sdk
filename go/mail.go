package veruapis

import (
	"context"
	"net/http"
	"net/url"
)

// MailService is folders, messages, drafts and sending.
type MailService struct{ client *Client }

// ListFolders returns every folder with its message and unread counts.
//
// Not paged: a mailbox has tens of folders, so paging would mean writing a loop
// that never runs twice.
func (s *MailService) ListFolders(ctx context.Context) ([]Folder, error) {
	out, _, err := Do[[]Folder](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/folders",
	})
	return out, err
}

// ListMessages returns one page of messages, newest first.
//
// At least one filter is required. Prefer Messages, which walks the pages.
func (s *MailService) ListMessages(ctx context.Context, opts ListMessagesOptions) ([]MessageSummary, Meta, error) {
	return Do[[]MessageSummary](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/messages",
		Query:  opts.query(),
	})
}

// Messages iterates every message matching the filter, following the cursor.
//
// Cursor rather than an offset: messages arriving mid-walk shift an offset, and
// a page gets skipped.
//
//	for msg, err := range api.Mail.Messages(ctx, opts) {
//		if err != nil {
//			return err
//		}
//		...
//	}
func (s *MailService) Messages(ctx context.Context, opts ListMessagesOptions) Seq2[MessageSummary] {
	return paginate[MessageSummary](ctx, s.client, "/v1/messages", opts.query())
}

// GetMessage returns one message with its headers and body decoded.
func (s *MailService) GetMessage(ctx context.Context, id string) (Message, error) {
	out, _, err := Do[Message](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/messages/" + url.PathEscape(id),
	})
	return out, err
}

// ListAttachments returns a message's attachment metadata. Bytes are a separate
// call, so listing never pulls a large file through the response.
func (s *MailService) ListAttachments(ctx context.Context, messageID string) ([]Attachment, error) {
	out, _, err := Do[[]Attachment](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/messages/" + url.PathEscape(messageID) + "/attachments",
	})
	return out, err
}

// ListDrafts returns one page of drafts.
func (s *MailService) ListDrafts(ctx context.Context, limit int, cursor string) ([]MessageSummary, Meta, error) {
	return Do[[]MessageSummary](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/drafts",
		Query:  query(map[string]any{"limit": limit, "cursor": cursor}),
	})
}

// Send submits a message as the key's owner.
//
// The API answers 202: the message is queued and archived in Sent, and delivery
// happens afterwards. That is not a promise it arrived, and a failure comes
// back later as a bounce rather than as an error here.
//
// An Idempotency-Key is generated unless one is supplied, so a retry cannot
// send the same message twice.
func (s *MailService) Send(ctx context.Context, message SendMessage, idempotencyKey ...string) (Sent, error) {
	req := Request{
		Method: http.MethodPost,
		Path:   "/v1/messages/send",
		Body:   message,
	}
	if len(idempotencyKey) > 0 {
		req.IdempotencyKey = idempotencyKey[0]
	}

	out, _, err := Do[Sent](ctx, s.client, req)
	return out, err
}

// query renders the filter.
func (o ListMessagesOptions) query() url.Values {
	return query(map[string]any{
		"folder_id": o.FolderID,
		"label":     o.Label,
		"flag":      o.Flag,
		"unread":    o.Unread,
		"limit":     o.Limit,
		"cursor":    o.Cursor,
	})
}
