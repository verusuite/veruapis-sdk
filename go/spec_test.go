package veruapis

import (
	"context"
	"encoding/json"
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
var ids = map[string]bool{"f1": true, "m1": true, "c1": true, "e1": true}

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
