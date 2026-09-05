package veruapis

import (
	"context"
	"iter"
	"net/http"
	"net/url"
)

// Seq2 is an iterator over values that may fail partway.
//
// iter.Seq2 from the standard library, aliased so the signatures in this
// package read as one thing rather than as a two-parameter generic:
//
//	for msg, err := range api.Mail.Messages(ctx, opts) {
//		if err != nil {
//			return err
//		}
//		...
//	}
//
// The error is a value in the sequence rather than something returned at the
// end, because a walk that fails on page four has already yielded three pages
// and the caller needs to know where it stopped.
type Seq2[T any] = iter.Seq2[T, error]

// paginate walks a cursor-paged listing.
//
// Cursor rather than an offset: rows arriving mid-walk shift an offset and a
// page gets skipped, which is a data-loss bug that looks like nothing at all.
func paginate[T any](ctx context.Context, c *Client, path string, base url.Values) Seq2[T] {
	return func(yield func(T, error) bool) {
		var zero T
		cursor := base.Get("cursor")

		for {
			q := url.Values{}
			for key, values := range base {
				if key == "cursor" {
					continue
				}
				q[key] = values
			}
			if cursor != "" {
				q.Set("cursor", cursor)
			}

			page, meta, err := Do[[]T](ctx, c, Request{
				Method: http.MethodGet,
				Path:   path,
				Query:  q,
			})
			if err != nil {
				yield(zero, err)
				return
			}

			for _, item := range page {
				if !yield(item, nil) {
					return
				}
			}

			// An empty page with a cursor would loop forever, so the absence of
			// rows ends the walk as surely as the absence of a cursor.
			if meta.NextCursor == "" || len(page) == 0 {
				return
			}
			cursor = meta.NextCursor
		}
	}
}
