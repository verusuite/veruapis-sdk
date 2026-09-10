package veruapis

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"sort"
	"strings"
	"testing"
)

// The client, checked against the API's own description.
//
// Nothing here is generated, so this is what keeps the hand-written client
// honest. A route this client calls that the API does not serve is a 404
// waiting for whoever calls that method, and it fails the build.
//
// The routes are collected by *calling every method* against a recording
// server, not by scanning the source. The first version of this test did scan,
// and it was wrong: a path built as "/v1/calendars/" + id + "/events/" + id
// gave up its first literal only, so real routes were reported as unwrapped and
// the check passed on truncated prefixes that happened to match. Running the
// client is exact, and it exercises every method as a side effect.
//
// It reads the JSON rather than the YAML so the module needs no dependency for
// it. Both are written by one run of "veruapis spec" and cannot disagree.

type openAPI struct {
	Paths map[string]map[string]struct {
		OperationID string `json:"operationId"`
		Deprecated  bool   `json:"deprecated"`
	} `json:"paths"`
}

func loadSpec(t *testing.T) openAPI {
	t.Helper()

	path := filepath.Join("..", "spec", "openapi.json")
	raw, err := os.ReadFile(path)
	if err != nil {
		t.Skipf("no specification at %s (%v); regenerate with\n"+
			"  cd ../../veruapis && go run ./cmd/veruapis spec ../veruapis-sdks/spec/openapi.yaml",
			path, err)
	}

	var doc openAPI
	if err := json.Unmarshal(raw, &doc); err != nil {
		t.Fatalf("the specification is not valid JSON: %v", err)
	}
	if len(doc.Paths) == 0 {
		t.Fatal("the specification describes no paths")
	}
	return doc
}

// everyCall is one invocation of every method this client offers.
//
// Adding a method here is the price of adding one to the client, and it is the
// right price: an unlisted method is one nothing has ever called, which is how
// a typo in a path ships.
func everyCall(ctx context.Context, api *Client) []func() {
	return []func(){
		func() { api.Mail.ListFolders(ctx) },
		func() { api.Mail.ListMessages(ctx, ListMessagesOptions{FolderID: []string{"f1"}}) },
		func() { api.Mail.GetMessage(ctx, "m1") },
		func() { api.Mail.ListAttachments(ctx, "m1") },
		func() { api.Mail.ListDrafts(ctx, 10, "") },
		func() { api.Mail.Send(ctx, SendMessage{To: []string{"a@b.example"}}) },

		func() { api.Calendar.ListCalendars(ctx) },
		func() { api.Calendar.GetCalendar(ctx, "c1") },
		func() { api.Calendar.ListEvents(ctx, ListEventsOptions{Start: "s", End: "e"}) },
		func() { api.Calendar.GetEvent(ctx, "c1", "e1") },
		func() { api.Calendar.CreateEvent(ctx, "c1", Event{Summary: "x"}) },
		func() { api.Calendar.UpdateEvent(ctx, "c1", "e1", Event{Summary: "y"}) },
		func() { api.Calendar.DeleteEvent(ctx, "c1", "e1") },
		func() { api.Calendar.FreeBusy(ctx, []string{"a@b.example"}, "s", "e") },

		func() { api.Spreadsheets.Get(ctx, "s1") },
		func() { api.Spreadsheets.State(ctx, "s1") },
		func() { api.Spreadsheets.Values(ctx, "s1", "r1", RenderComputed) },
		func() { api.Spreadsheets.BatchValues(ctx, "s1", []string{"Sheet1!A1:B2"}, "") },
		func() { api.Spreadsheets.Write(ctx, "s1", "r1", [][]any{{"a"}}) },
		func() {
			api.Spreadsheets.BatchWrite(ctx, "s1", []ValueRange{{Range: "Sheet1!A1", Values: [][]any{{"a"}}}})
		},
		func() { api.Spreadsheets.Append(ctx, "s1", "r1", [][]any{{"a"}}) },
		func() { api.Spreadsheets.Clear(ctx, "s1", "r1") },
		func() {
			api.Spreadsheets.ApplyStructure(ctx, "s1", "state-token", []map[string]any{
				{"add_sheet": map[string]any{"title": "Q4"}},
			})
		},

		func() { api.Identity.Me(ctx) },
		func() { api.Identity.ListGroups(ctx, 25, "") },

		func() { api.Contacts.ListAddressBooks(ctx) },
		func() { api.Contacts.ListContacts(ctx, ListContactsOptions{Limit: 25}) },
		func() { api.Contacts.GetContact(ctx, "k1") },
		func() { api.Contacts.CreateContact(ctx, Contact{Name: "Bob"}) },
		func() { api.Contacts.UpdateContact(ctx, "k1", Contact{Title: "Buyer"}) },
		func() { api.Contacts.DeleteContact(ctx, "k1") },

		func() { api.Documents.ListDocuments(ctx, ListDocumentsOptions{Limit: 25}) },
		func() { api.Documents.GetDocument(ctx, "d1") },
		func() { api.Documents.CreateDocument(ctx, Document{Type: TypeSpreadsheet}) },
		func() { api.Documents.UpdateDocument(ctx, "d1", Document{Title: "x"}) },
		func() { api.Documents.DeleteDocument(ctx, "d1") },
		func() { api.Documents.RestoreDocument(ctx, "d1") },
		func() { api.Documents.CopyDocument(ctx, "d1", Document{}) },
		func() { api.Documents.ListComments(ctx, "d1", true) },
		func() { api.Documents.CreateComment(ctx, "d1", Comment{Body: "x"}) },
		func() { api.Documents.UpdateComment(ctx, "n1", Comment{State: "resolved"}) },
		func() { api.Documents.DeleteComment(ctx, "n1") },

		func() { api.Files.ListFolders(ctx, false) },
		func() { api.Files.CreateFolder(ctx, FileFolder{Name: "Reports"}) },
		func() { api.Files.UpdateFolder(ctx, "o1", "Archive", "", false) },
		func() { api.Files.DeleteFolder(ctx, "o1") },
		func() { api.Files.ListFiles(ctx, ListFilesOptions{Limit: 25}) },
		func() { api.Files.GetFile(ctx, "b1") },
		func() { api.Files.Download(ctx, "b1", "") },
		func() { api.Files.UpdateFile(ctx, "b1", "new-name.pdf", "", false) },
		func() { api.Files.DeleteFile(ctx, "b1") },
		func() { api.Files.StartUpload(ctx, NewUpload{Filename: "f.pdf", Size: 10}) },
		func() { api.Files.UploadPart(ctx, "u1", 1, []byte("bytes")) },
		func() { api.Files.UploadStatus(ctx, "u1") },
		func() { api.Files.CompleteUpload(ctx, "u1") },
		func() { api.Files.AbortUpload(ctx, "u1") },
		func() { api.Files.ListPermissions(ctx, "b1") },
		func() { api.Files.Share(ctx, "b1", Permission{PrincipalID: "usr1", Role: "editor"}) },
		func() { api.Files.Unshare(ctx, "b1", "p1") },

		// The iterators, drained so their first request is made.
		func() {
			for range api.Mail.Messages(ctx, ListMessagesOptions{FolderID: []string{"f1"}}) {
				break
			}
		},
		func() {
			for range api.Calendar.Events(ctx, ListEventsOptions{Start: "s", End: "e"}) {
				break
			}
		},
		func() {
			for range api.Identity.Groups(ctx) {
				break
			}
		},
		func() {
			for range api.Contacts.Contacts(ctx, ListContactsOptions{}) {
				break
			}
		},
		func() {
			for range api.Documents.Documents(ctx, ListDocumentsOptions{}) {
				break
			}
		},
		func() {
			for range api.Files.Files(ctx, ListFilesOptions{}) {
				break
			}
		},
	}
}

// routesTheClientCalls runs every method and records what it asked for.
func routesTheClientCalls(t *testing.T) map[string]bool {
	t.Helper()

	routes := map[string]bool{}

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		routes[r.Method+" "+templated(r.URL.Path)] = true
		w.Header().Set("Content-Type", "application/json")
		// An empty list satisfies every return shape here, and an absent cursor
		// stops the iterators after one page.
		w.Write([]byte(`{"data":[],"meta":{"request_id":"r","timestamp":"t"}}`))
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	for _, call := range everyCall(context.Background(), api) {
		call()
	}

	if len(routes) == 0 {
		t.Fatal("no routes were recorded; the test harness is broken, not the client")
	}
	return routes
}

// ids are the values everyCall passes, so a concrete path can be turned back
// into the templated one the specification describes.
//
// A segment walk rather than a regular expression: Go's RE2 has no lookahead,
// and "replace /m1 with /{}" without one would also rewrite a path that merely
// started with those characters.
//
// A range is an id here too: it is a path segment the caller supplies, and
// "r1" stands in for the A1 notation a real call carries.
var ids = map[string]bool{
	"f1": true, "m1": true, "c1": true, "e1": true,
	"s1": true, "r1": true,
	"k1": true, "d1": true, "n1": true, "o1": true,
	"b1": true, "u1": true, "p1": true, "1": true,
}

func templated(path string) string {
	segments := strings.Split(path, "/")
	for i, segment := range segments {
		if ids[segment] {
			segments[i] = "{}"
		}
	}
	return strings.TrimSuffix(strings.Join(segments, "/"), "/")
}

// documented is every route the API serves, excluding the ones it describes but
// does not implement.
func documented(doc openAPI) map[string]bool {
	out := map[string]bool{}
	for path, methods := range doc.Paths {
		for method, op := range methods {
			if op.Deprecated {
				continue
			}
			normalised := regexp.MustCompile(`\{[^}]*\}`).ReplaceAllString(path, "{}")
			out[strings.ToUpper(method)+" "+strings.TrimSuffix(normalised, "/")] = true
		}
	}
	return out
}

func TestEveryRouteTheClientCallsIsDocumented(t *testing.T) {
	spec := documented(loadSpec(t))
	used := routesTheClientCalls(t)

	var unknown []string
	for route := range used {
		if !spec[route] {
			unknown = append(unknown, route)
		}
	}
	sort.Strings(unknown)

	for _, route := range unknown {
		t.Errorf("the client calls %s, which the API does not serve", route)
	}
	if len(unknown) > 0 {
		t.Log("These are 404s waiting to happen. Fix the client, or regenerate " +
			"the specification if the API really did change.")
	}
}

func TestEndpointsNotWrappedYet(t *testing.T) {
	spec := documented(loadSpec(t))
	used := routesTheClientCalls(t)

	var missing []string
	for route := range spec {
		if !used[route] {
			missing = append(missing, route)
		}
	}
	sort.Strings(missing)

	// Reported, not failed: the client is allowed to lag, and Do reaches
	// anything it has not wrapped.
	if len(missing) > 0 {
		t.Logf("%d of %d endpoint(s) not wrapped yet (Do reaches them):", len(missing), len(spec))
		for _, route := range missing {
			t.Logf("  %s", route)
		}
	}
}

// TestAStructuralChangeSendsItsPrecondition.
//
// The one endpoint in this client that carries a header. Without If-Match the
// API refuses the request outright, so a client that dropped it would compile,
// call, and fail every time — and the failure would look like a server problem
// rather than a missing header.
func TestAStructuralChangeSendsItsPrecondition(t *testing.T) {
	var got string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got = r.Header.Get("If-Match")
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"data":{"state":"b2","changed":true},"meta":{"request_id":"r","timestamp":"t"}}`))
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	if _, err := api.Spreadsheets.ApplyStructure(context.Background(), "s1", "a1b2c3",
		[]map[string]any{{"add_sheet": map[string]any{"title": "Q4"}}}); err != nil {
		t.Fatal(err)
	}

	if got != "a1b2c3" {
		t.Errorf("If-Match = %q, want the state the caller read", got)
	}
}

// TestARangeIsOneSegment. A1 notation carries characters a URL reads as
// structure — a quoted sheet name, a space, a colon — and a range that split
// into two segments would address a route that does not exist.
func TestARangeIsOneSegment(t *testing.T) {
	var path string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		path = r.URL.EscapedPath()
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"data":{},"meta":{"request_id":"r","timestamp":"t"}}`))
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	if _, err := api.Spreadsheets.Values(context.Background(), "s1", "'Q1 2026'!A1:D20", ""); err != nil {
		t.Fatal(err)
	}

	if strings.Count(strings.TrimPrefix(path, "/v1/spreadsheets/s1/values/"), "/") != 0 {
		t.Errorf("the range became more than one segment: %s", path)
	}
	if !strings.Contains(path, "%20") {
		t.Errorf("the space in the sheet name was not escaped: %s", path)
	}
}

// TestAPartIsSentAsBytesRatherThanJSON.
//
// The one call in this client whose body is not JSON. Encoding a file as JSON
// would inflate it and corrupt anything that is not valid UTF-8, which is most
// of what people upload.
func TestAPartIsSentAsBytesRatherThanJSON(t *testing.T) {
	var got []byte
	var contentType string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		got, _ = io.ReadAll(r.Body)
		contentType = r.Header.Get("Content-Type")
		w.Header().Set("Content-Type", "application/json")
		w.Write([]byte(`{"data":{"part":2,"etag":"e"},"meta":{"request_id":"r","timestamp":"t"}}`))
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	receipt, err := api.Files.UploadPart(context.Background(), "u1", 2, []byte{0x00, 0xff, 0x10})
	if err != nil {
		t.Fatal(err)
	}

	if !bytes.Equal(got, []byte{0x00, 0xff, 0x10}) {
		t.Errorf("body = %v, want the bytes as they were given", got)
	}
	if contentType != "application/octet-stream" {
		t.Errorf("content type = %q", contentType)
	}
	if receipt.Part != 2 {
		t.Errorf("part = %d, want the receipt decoded", receipt.Part)
	}
}

// TestADownloadReturnsBytesAndCarriesARange.
//
// A download answers a file rather than an envelope, and a range is how one
// larger than the proxy's ceiling is fetched at all.
func TestADownloadReturnsBytesAndCarriesARange(t *testing.T) {
	var rangeHeader string

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		rangeHeader = r.Header.Get("Range")
		w.Header().Set("Content-Type", "application/pdf")
		w.WriteHeader(http.StatusPartialContent)
		w.Write([]byte{0x25, 0x50, 0x44, 0x46})
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	raw, contentType, err := api.Files.Download(context.Background(), "b1", "bytes=0-3")
	if err != nil {
		t.Fatal(err)
	}

	if rangeHeader != "bytes=0-3" {
		t.Errorf("Range = %q, want the one the caller asked for", rangeHeader)
	}
	if contentType != "application/pdf" {
		t.Errorf("content type = %q", contentType)
	}
	if !bytes.Equal(raw, []byte{0x25, 0x50, 0x44, 0x46}) {
		t.Errorf("bytes = %v", raw)
	}
}

// TestARefusedDownloadIsStillAnAPIError. The response is bytes on success and
// an envelope on failure, so a caller reads the same error type whichever call
// refused them.
func TestARefusedDownloadIsStillAnAPIError(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusForbidden)
		w.Write([]byte(`{"error":{"code":"insufficient_scope","message":"no files.read"}}`))
	}))
	defer server.Close()

	api := New("vak_live_test_secret", WithBaseURL(server.URL), WithMaxRetries(0))
	_, _, err := api.Files.Download(context.Background(), "b1", "")

	var apiErr *Error
	if !errors.As(err, &apiErr) {
		t.Fatalf("err = %T, want *Error", err)
	}
	if apiErr.Code != "insufficient_scope" {
		t.Errorf("code = %q", apiErr.Code)
	}
}
