package veruapis

import (
	"context"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"testing"
	"time"
)

// The transport.
//
// Every test drives a recording server rather than the network, so what is
// asserted is the exact request this client builds: the header it sends, the
// shape of a repeated query parameter, whether a failure was retried. A test
// against a live API would assert the API instead, and would pass for the wrong
// reason the day the API changed.

type capture struct {
	Method  string
	Path    string
	Query   url.Values
	Header  http.Header
	Body    string
	Attempt int
}

// recorder builds a client over a server that records and answers to script.
//
// Sleeping is replaced, so a test asserting three retries takes microseconds
// rather than the seconds the backoff would otherwise cost.
func recorder(t *testing.T, respond func(w http.ResponseWriter, r *http.Request, attempt int), opts ...Option) (*Client, *[]capture) {
	t.Helper()

	var calls []capture

	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		var body []byte
		if r.Body != nil {
			body, _ = io.ReadAll(r.Body)
		}

		attempt := len(calls)
		calls = append(calls, capture{
			Method: r.Method, Path: r.URL.Path, Query: r.URL.Query(),
			Header: r.Header.Clone(), Body: string(body), Attempt: attempt,
		})
		respond(w, r, attempt)
	}))
	t.Cleanup(server.Close)

	options := append([]Option{WithBaseURL(server.URL), WithMaxRetries(0)}, opts...)
	api := New("vak_live_abc_secret", options...)

	// No real waiting: the delay is asserted by what was requested, not by how
	// long the test took.
	api.sleep = func(ctx context.Context, _ time.Duration) error {
		if err := ctx.Err(); err != nil {
			return err
		}
		return nil
	}

	return api, &calls
}

func jsonOK(w http.ResponseWriter, body string) {
	w.Header().Set("Content-Type", "application/json")
	_, _ = w.Write([]byte(body))
}

func jsonError(w http.ResponseWriter, status int, code string) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_, _ = w.Write([]byte(`{"error":{"code":"` + code +
		`","message":"Refused.","request_id":"req_err"}}`))
}

const emptyList = `{"data":[],"meta":{"request_id":"r","timestamp":"t"}}`

func TestNoKeyIsRefusedBeforeAnyRequest(t *testing.T) {
	api := New("")

	_, err := api.Mail.ListFolders(context.Background())
	if err == nil || !strings.Contains(err.Error(), "no API key") {
		t.Fatalf("err = %v, want a complaint about the missing key", err)
	}
}

func TestTheKeyIsSentAsABearerToken(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	if _, err := api.Mail.ListFolders(context.Background()); err != nil {
		t.Fatal(err)
	}

	got := (*calls)[0].Header
	if got.Get("Authorization") != "Bearer vak_live_abc_secret" {
		t.Errorf("Authorization = %q", got.Get("Authorization"))
	}
	if got.Get("Accept") != "application/json" {
		t.Errorf("Accept = %q", got.Get("Accept"))
	}
}

func TestTheEnvelopeIsUnwrapped(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":[{"id":"f1","name":"INBOX","unread_count":3}],`+
			`"meta":{"request_id":"req_1","timestamp":"t"}}`)
	})

	folders, err := api.Mail.ListFolders(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(folders) != 1 || folders[0].Name != "INBOX" || folders[0].UnreadCount != 3 {
		t.Fatalf("folders = %+v", folders)
	}
}

func TestTheMetaIsAvailableWhenAsked(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":[],"meta":{"request_id":"req_xyz","timestamp":"t","next_cursor":"c2"}}`)
	})

	_, meta, err := api.Mail.ListMessages(context.Background(), ListMessagesOptions{FolderID: []string{"f1"}})
	if err != nil {
		t.Fatal(err)
	}
	if meta.RequestID != "req_xyz" || meta.NextCursor != "c2" {
		t.Fatalf("meta = %+v", meta)
	}
}

func TestAListParameterIsRepeatedNotJoined(t *testing.T) {
	// Comma-joining would be read by the API as one folder id, which returns an
	// empty list rather than an error and looks like there is no mail.
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	_, _, err := api.Mail.ListMessages(context.Background(), ListMessagesOptions{
		FolderID: []string{"a", "b"},
	})
	if err != nil {
		t.Fatal(err)
	}

	if got := (*calls)[0].Query["folder_id"]; len(got) != 2 || got[0] != "a" || got[1] != "b" {
		t.Fatalf("folder_id = %v, want two separate values", got)
	}
}

func TestEmptyOptionsAreLeftOut(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	_, _, err := api.Mail.ListMessages(context.Background(), ListMessagesOptions{
		FolderID: []string{"f1"},
	})
	if err != nil {
		t.Fatal(err)
	}

	q := (*calls)[0].Query
	for _, absent := range []string{"cursor", "limit", "unread", "flag", "label"} {
		if q.Has(absent) {
			t.Errorf("%s was sent as %q despite being unset", absent, q.Get(absent))
		}
	}
}

func TestEveryOptionIsSentWhenSet(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	_, _, err := api.Mail.ListMessages(context.Background(), ListMessagesOptions{
		FolderID: []string{"f1"},
		Label:    []string{"work"},
		Flag:     "starred",
		Unread:   true,
		Limit:    25,
		Cursor:   "c9",
	})
	if err != nil {
		t.Fatal(err)
	}

	q := (*calls)[0].Query
	for key, want := range map[string]string{
		"flag": "starred", "unread": "true", "limit": "25", "cursor": "c9", "label": "work",
	} {
		if q.Get(key) != want {
			t.Errorf("%s = %q, want %q", key, q.Get(key), want)
		}
	}
}

func TestAnIDIsEscapedIntoThePath(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":{"id":"m1"},"meta":{"request_id":"r","timestamp":"t"}}`)
	})

	if _, err := api.Mail.GetMessage(context.Background(), "a/b"); err != nil {
		t.Fatal(err)
	}

	// The server sees the decoded path; what matters is that it stayed one
	// segment rather than walking into another route.
	if got := (*calls)[0].Path; got != "/v1/messages/a/b" {
		t.Errorf("path = %q", got)
	}
}

func TestAWriteCarriesAnIdempotencyKey(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":{"id":"m1"},"meta":{"request_id":"r","timestamp":"t"}}`)
	})

	if _, err := api.Mail.Send(context.Background(), SendMessage{To: []string{"a@b.example"}}); err != nil {
		t.Fatal(err)
	}

	if got := (*calls)[0].Header.Get("Idempotency-Key"); len(got) != 36 {
		t.Errorf("Idempotency-Key = %q, want a generated value", got)
	}
}

func TestTheCallersIdempotencyKeyIsUsed(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":{"id":"m1"},"meta":{"request_id":"r","timestamp":"t"}}`)
	})

	_, err := api.Mail.Send(context.Background(), SendMessage{To: []string{"a@b.example"}}, "mine")
	if err != nil {
		t.Fatal(err)
	}

	if got := (*calls)[0].Header.Get("Idempotency-Key"); got != "mine" {
		t.Errorf("Idempotency-Key = %q, want the caller's", got)
	}
}

func TestAReadCarriesNoIdempotencyKey(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	if _, err := api.Mail.ListFolders(context.Background()); err != nil {
		t.Fatal(err)
	}
	if got := (*calls)[0].Header.Get("Idempotency-Key"); got != "" {
		t.Errorf("a read carried Idempotency-Key %q", got)
	}
}

func TestOneKeyAcrossEveryRetry(t *testing.T) {
	// A retry that minted a new key would be a second request as far as the
	// server is concerned, which defeats the point of sending one.
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		if attempt == 0 {
			jsonError(w, http.StatusInternalServerError, "internal_error")
			return
		}
		jsonOK(w, `{"data":{"id":"m1"},"meta":{"request_id":"r","timestamp":"t"}}`)
	}, WithMaxRetries(1))

	if _, err := api.Mail.Send(context.Background(), SendMessage{To: []string{"a@b.example"}}); err != nil {
		t.Fatal(err)
	}

	first := (*calls)[0].Header.Get("Idempotency-Key")
	second := (*calls)[1].Header.Get("Idempotency-Key")
	if first == "" || first != second {
		t.Errorf("keys differed across a retry: %q then %q", first, second)
	}
}

func TestTheBodyIsSentAsJSON(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":{"id":"m1"},"meta":{"request_id":"r","timestamp":"t"}}`)
	})

	_, err := api.Mail.Send(context.Background(), SendMessage{
		To: []string{"a@b.example"}, Subject: "Hi", Text: "There",
	})
	if err != nil {
		t.Fatal(err)
	}

	call := (*calls)[0]
	if call.Header.Get("Content-Type") != "application/json" {
		t.Errorf("Content-Type = %q", call.Header.Get("Content-Type"))
	}
	if !strings.Contains(call.Body, `"subject":"Hi"`) {
		t.Errorf("body = %s", call.Body)
	}
}

func TestNoContentIsNotDecoded(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		w.WriteHeader(http.StatusNoContent)
	})

	if err := api.Calendar.DeleteEvent(context.Background(), "c1", "e1"); err != nil {
		t.Fatalf("a 204 was treated as a failure: %v", err)
	}
}

func TestAnEmptyBodyOnSuccessIsNotDecoded(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		w.WriteHeader(http.StatusOK)
	})

	if err := api.Calendar.DeleteEvent(context.Background(), "c1", "e1"); err != nil {
		t.Fatalf("an empty 200 was treated as a failure: %v", err)
	}
}

// ---- errors ----

func TestAnErrorCarriesTheCodeAndRequestID(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonError(w, http.StatusForbidden, "insufficient_scope")
	})

	_, err := api.Mail.ListFolders(context.Background())

	var apiErr *Error
	if !errors.As(err, &apiErr) {
		t.Fatalf("err = %v, want an *Error reachable through errors.As", err)
	}
	if apiErr.Code != "insufficient_scope" || apiErr.Status != 403 || apiErr.RequestID != "req_err" {
		t.Fatalf("error = %+v", apiErr)
	}
	if !apiErr.IsPermissionProblem() {
		t.Error("a 403 is not reported as a permission problem")
	}
	if !strings.Contains(apiErr.Error(), "req_err") {
		t.Errorf("Error() omits the request id: %s", apiErr.Error())
	}
}

func TestTheErrorHelpersMatchTheStatus(t *testing.T) {
	cases := map[int]func(*Error) bool{
		http.StatusUnauthorized:    (*Error).IsAuthProblem,
		http.StatusForbidden:       (*Error).IsPermissionProblem,
		http.StatusNotFound:        (*Error).IsNotFound,
		http.StatusTooManyRequests: (*Error).IsRateLimited,
	}

	for status, helper := range cases {
		api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
			jsonError(w, status, "refused")
		})

		_, err := api.Mail.ListFolders(context.Background())

		var apiErr *Error
		if !errors.As(err, &apiErr) {
			t.Fatalf("%d: err = %v", status, err)
		}
		if !helper(apiErr) {
			t.Errorf("%d: the matching helper returned false", status)
		}
	}
}

func TestErrorWithoutARequestIDStillReads(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusBadRequest)
		_, _ = w.Write([]byte(`{"error":{"code":"malformed_request","message":"Bad."}}`))
	})

	_, err := api.Mail.ListFolders(context.Background())

	var apiErr *Error
	if !errors.As(err, &apiErr) {
		t.Fatal(err)
	}
	if strings.Contains(apiErr.Error(), "request ") {
		t.Errorf("Error() invented a request id: %s", apiErr.Error())
	}
}

func TestFieldDetailsSurvive(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(http.StatusUnprocessableEntity)
		_, _ = w.Write([]byte(`{"error":{"code":"validation_error","message":"Bad.",` +
			`"request_id":"r","details":[{"field":"to","message":"required"}]}}`))
	})

	_, err := api.Mail.Send(context.Background(), SendMessage{})

	var apiErr *Error
	if !errors.As(err, &apiErr) {
		t.Fatal(err)
	}
	if len(apiErr.Details) != 1 || apiErr.Details[0].Field != "to" {
		t.Fatalf("details = %+v", apiErr.Details)
	}
}

func TestABodyThatIsNotJSONIsReportedHonestly(t *testing.T) {
	// A proxy or an error page answered instead of the API. Saying so beats a
	// decode failure nobody can act on.
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		w.WriteHeader(http.StatusBadGateway)
		_, _ = w.Write([]byte("<html>502</html>"))
	})

	_, err := api.Mail.ListFolders(context.Background())

	var apiErr *Error
	if !errors.As(err, &apiErr) {
		t.Fatal(err)
	}
	if apiErr.Code != "unreadable_response" || apiErr.Status != 502 {
		t.Fatalf("error = %+v", apiErr)
	}
}

func TestAMalformedSuccessBodyIsADecodeError(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data": not json`)
	})

	_, err := api.Mail.ListFolders(context.Background())
	if err == nil || !strings.Contains(err.Error(), "decode") {
		t.Fatalf("err = %v, want a decode failure", err)
	}
}

// ---- retries ----

func TestARateLimitIsRetried(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		if attempt == 0 {
			jsonError(w, http.StatusTooManyRequests, "rate_limited")
			return
		}
		jsonOK(w, `{"data":[{"id":"f1"}],"meta":{"request_id":"r","timestamp":"t"}}`)
	}, WithMaxRetries(2))

	folders, err := api.Mail.ListFolders(context.Background())
	if err != nil {
		t.Fatal(err)
	}
	if len(folders) != 1 || len(*calls) != 2 {
		t.Fatalf("%d folders after %d calls", len(folders), len(*calls))
	}
}

func TestRetriesStopAtTheLimit(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonError(w, http.StatusInternalServerError, "internal_error")
	}, WithMaxRetries(2))

	if _, err := api.Mail.ListFolders(context.Background()); err == nil {
		t.Fatal("a persistent 500 succeeded")
	}
	// The first attempt plus two retries.
	if len(*calls) != 3 {
		t.Errorf("made %d calls, want 3", len(*calls))
	}
}

func TestARefusalIsNeverRetried(t *testing.T) {
	for _, status := range []int{http.StatusBadRequest, http.StatusForbidden, http.StatusNotFound} {
		api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
			jsonError(w, status, "refused")
		}, WithMaxRetries(3))

		if _, err := api.Mail.ListFolders(context.Background()); err == nil {
			t.Fatalf("%d succeeded", status)
		}
		if len(*calls) != 1 {
			t.Errorf("%d was retried %d times; it fails the same way every time",
				status, len(*calls)-1)
		}
	}
}

func TestRetryAfterInSecondsIsUsed(t *testing.T) {
	var waited time.Duration

	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		if attempt == 0 {
			w.Header().Set("Retry-After", "7")
			jsonError(w, http.StatusTooManyRequests, "rate_limited")
			return
		}
		jsonOK(w, emptyList)
	}, WithMaxRetries(1))

	api.sleep = func(_ context.Context, d time.Duration) error {
		waited = d
		return nil
	}

	if _, err := api.Mail.ListFolders(context.Background()); err != nil {
		t.Fatal(err)
	}
	if waited != 7*time.Second {
		t.Errorf("waited %v, want the 7s the server asked for", waited)
	}
}

func TestRetryAfterAsADateIsUsed(t *testing.T) {
	var waited time.Duration

	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		if attempt == 0 {
			w.Header().Set("Retry-After", time.Now().Add(30*time.Second).UTC().Format(http.TimeFormat))
			jsonError(w, http.StatusTooManyRequests, "rate_limited")
			return
		}
		jsonOK(w, emptyList)
	}, WithMaxRetries(1))

	api.sleep = func(_ context.Context, d time.Duration) error {
		waited = d
		return nil
	}

	if _, err := api.Mail.ListFolders(context.Background()); err != nil {
		t.Fatal(err)
	}
	// Roughly 30 seconds, allowing for the time the test itself took.
	if waited < 25*time.Second || waited > 31*time.Second {
		t.Errorf("waited %v, want about 30s", waited)
	}
}

func TestAnUnusableRetryAfterFallsBackToBackoff(t *testing.T) {
	for _, header := range []string{"soon", "-5", time.Now().Add(-time.Hour).UTC().Format(http.TimeFormat)} {
		var waited time.Duration

		api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
			if attempt == 0 {
				w.Header().Set("Retry-After", header)
				jsonError(w, http.StatusTooManyRequests, "rate_limited")
				return
			}
			jsonOK(w, emptyList)
		}, WithMaxRetries(1))

		api.sleep = func(_ context.Context, d time.Duration) error {
			waited = d
			return nil
		}

		if _, err := api.Mail.ListFolders(context.Background()); err != nil {
			t.Fatalf("%q: %v", header, err)
		}
		if len(*calls) != 2 {
			t.Errorf("%q: made %d calls, want a retry", header, len(*calls))
		}
		if waited <= 0 {
			t.Errorf("%q: waited %v, want a backoff", header, waited)
		}
	}
}

func TestANetworkFailureIsRetried(t *testing.T) {
	attempts := 0

	api := New("k", WithBaseURL("http://127.0.0.1:1"), WithMaxRetries(2))
	api.httpClient = &http.Client{
		Transport: roundTripper(func(r *http.Request) (*http.Response, error) {
			attempts++
			return nil, errors.New("connection refused")
		}),
	}
	api.sleep = func(context.Context, time.Duration) error { return nil }

	if _, err := api.Mail.ListFolders(context.Background()); err == nil {
		t.Fatal("a persistent network failure succeeded")
	}
	if attempts != 3 {
		t.Errorf("tried %d times, want 3", attempts)
	}
}

func TestANetworkFailureThatRecovers(t *testing.T) {
	attempts := 0

	api := New("k", WithBaseURL("http://example.invalid"), WithMaxRetries(2))
	api.httpClient = &http.Client{
		Transport: roundTripper(func(r *http.Request) (*http.Response, error) {
			attempts++
			if attempts == 1 {
				return nil, errors.New("connection reset")
			}
			return &http.Response{
				StatusCode: 200,
				Header:     http.Header{"Content-Type": []string{"application/json"}},
				Body:       io_NopCloser(emptyList),
			}, nil
		}),
	}
	api.sleep = func(context.Context, time.Duration) error { return nil }

	if _, err := api.Mail.ListFolders(context.Background()); err != nil {
		t.Fatal(err)
	}
	if attempts != 2 {
		t.Errorf("tried %d times, want 2", attempts)
	}
}

func TestACancelledContextStopsRetrying(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())

	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		cancel()
		jsonError(w, http.StatusInternalServerError, "internal_error")
	}, WithMaxRetries(5))

	// The real sleep, so the cancelled context is what stops the loop.
	api.sleep = sleepContext

	if _, err := api.Mail.ListFolders(ctx); err == nil {
		t.Fatal("a cancelled call succeeded")
	}
	if len(*calls) > 2 {
		t.Errorf("kept retrying past the cancellation: %d calls", len(*calls))
	}
}

// ---- pagination ----

func TestPagingFollowsTheCursor(t *testing.T) {
	pages := []string{
		`{"data":[{"id":"1"}],"meta":{"request_id":"r","timestamp":"t","next_cursor":"c2"}}`,
		`{"data":[{"id":"2"}],"meta":{"request_id":"r","timestamp":"t","next_cursor":"c3"}}`,
		`{"data":[{"id":"3"}],"meta":{"request_id":"r","timestamp":"t"}}`,
	}

	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		jsonOK(w, pages[attempt])
	})

	var ids []string
	for msg, err := range api.Mail.Messages(context.Background(), ListMessagesOptions{FolderID: []string{"f1"}}) {
		if err != nil {
			t.Fatal(err)
		}
		ids = append(ids, msg.ID)
	}

	if strings.Join(ids, ",") != "1,2,3" {
		t.Fatalf("ids = %v", ids)
	}
	if (*calls)[0].Query.Has("cursor") {
		t.Error("the first page carried a cursor")
	}
	if (*calls)[1].Query.Get("cursor") != "c2" || (*calls)[2].Query.Get("cursor") != "c3" {
		t.Error("a page did not carry the previous page's cursor")
	}
	// The filter survives every page, or later pages would return the whole
	// mailbox.
	for i, call := range *calls {
		if call.Query.Get("folder_id") != "f1" {
			t.Errorf("page %d lost the folder filter", i)
		}
	}
}

func TestPagingStopsOnAnEmptyPage(t *testing.T) {
	// A cursor with no rows would otherwise loop forever.
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":[],"meta":{"request_id":"r","timestamp":"t","next_cursor":"always"}}`)
	})

	count := 0
	for range api.Mail.Messages(context.Background(), ListMessagesOptions{FolderID: []string{"f1"}}) {
		count++
	}

	if count != 0 || len(*calls) != 1 {
		t.Fatalf("yielded %d over %d calls", count, len(*calls))
	}
}

func TestPagingYieldsTheErrorItStoppedOn(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, attempt int) {
		if attempt == 0 {
			jsonOK(w, `{"data":[{"id":"1"}],"meta":{"request_id":"r","timestamp":"t","next_cursor":"c2"}}`)
			return
		}
		jsonError(w, http.StatusForbidden, "insufficient_scope")
	})

	var seen int
	var failure error

	for _, err := range api.Mail.Messages(context.Background(), ListMessagesOptions{FolderID: []string{"f1"}}) {
		if err != nil {
			failure = err
			break
		}
		seen++
	}

	if seen != 1 {
		t.Errorf("yielded %d rows before the failure, want the first page's 1", seen)
	}

	var apiErr *Error
	if !errors.As(failure, &apiErr) || apiErr.Code != "insufficient_scope" {
		t.Fatalf("failure = %v, want the API's refusal", failure)
	}
}

func TestPagingStopsWhenTheCallerBreaks(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, `{"data":[{"id":"1"},{"id":"2"}],"meta":{"request_id":"r","timestamp":"t","next_cursor":"more"}}`)
	})

	for range api.Calendar.Events(context.Background(), ListEventsOptions{Start: "s", End: "e"}) {
		break
	}

	if len(*calls) != 1 {
		t.Errorf("fetched %d pages after the caller stopped", len(*calls))
	}
}

func TestPagingCanStartFromACursor(t *testing.T) {
	api, calls := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	for range api.Mail.Messages(context.Background(), ListMessagesOptions{
		FolderID: []string{"f1"}, Cursor: "resume-here",
	}) {
	}

	if got := (*calls)[0].Query.Get("cursor"); got != "resume-here" {
		t.Errorf("cursor = %q, want the caller's", got)
	}
}

// ---- options ----

func TestOptionsAreApplied(t *testing.T) {
	custom := &http.Client{Timeout: time.Second}
	api := New("k",
		WithBaseURL("https://example.test/"),
		WithHTTPClient(custom),
		WithMaxRetries(9),
	)

	if api.baseURL != "https://example.test" {
		t.Errorf("baseURL = %q, want the trailing slash trimmed", api.baseURL)
	}
	if api.httpClient != custom {
		t.Error("the supplied http.Client was not used")
	}
	if api.maxRetries != 9 {
		t.Errorf("maxRetries = %d", api.maxRetries)
	}
}

func TestANegativeRetryCountIsIgnored(t *testing.T) {
	api := New("k", WithMaxRetries(-1))
	if api.maxRetries != 2 {
		t.Errorf("maxRetries = %d, want the default kept", api.maxRetries)
	}
}

func TestTheDefaultsAreProduction(t *testing.T) {
	api := New("k")
	if api.baseURL != DefaultBaseURL {
		t.Errorf("baseURL = %q", api.baseURL)
	}
	if api.httpClient == nil || api.maxRetries != 2 {
		t.Errorf("client = %v, retries = %d", api.httpClient, api.maxRetries)
	}
}

func TestABodyThatCannotBeEncodedIsReported(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	// A channel cannot be marshalled, and the failure should name encoding
	// rather than surfacing as a confusing transport error.
	_, _, err := Do[struct{}](context.Background(), api, Request{
		Method: http.MethodPost, Path: "/v1/anything", Body: make(chan int),
	})
	if err == nil || !strings.Contains(err.Error(), "encode body") {
		t.Fatalf("err = %v, want an encoding failure", err)
	}
}

func TestAnUnusableMethodIsReported(t *testing.T) {
	api, _ := recorder(t, func(w http.ResponseWriter, _ *http.Request, _ int) {
		jsonOK(w, emptyList)
	})

	_, _, err := Do[struct{}](context.Background(), api, Request{
		Method: "in valid", Path: "/v1/anything",
	})
	if err == nil || !strings.Contains(err.Error(), "build request") {
		t.Fatalf("err = %v, want a request-building failure", err)
	}
}

func TestSleepContextGivesUpWithTheCaller(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	cancel()

	if err := sleepContext(ctx, time.Hour); err == nil {
		t.Fatal("sleeping ignored a cancelled context")
	}
	if err := sleepContext(context.Background(), time.Millisecond); err != nil {
		t.Fatalf("a short sleep failed: %v", err)
	}
}

func TestBackoffGrowsAndIsBounded(t *testing.T) {
	for attempt := range 12 {
		d := backoff(attempt)
		if d <= 0 || d > 8*time.Second {
			t.Fatalf("backoff(%d) = %v, want a positive value under the cap", attempt, d)
		}
	}
}

func TestQuerySkipsNilAndPassesTheRest(t *testing.T) {
	q := query(map[string]any{
		"nothing": nil,
		"float":   1.5,
	})

	if q.Has("nothing") {
		t.Error("a nil value was sent")
	}
	if q.Get("float") != "1.5" {
		t.Errorf("float = %q", q.Get("float"))
	}
}

// roundTripper lets a test stand in for the network without a server.
type roundTripper func(*http.Request) (*http.Response, error)

func (f roundTripper) RoundTrip(r *http.Request) (*http.Response, error) { return f(r) }

// io_NopCloser builds a response body from a string.
func io_NopCloser(s string) io.ReadCloser { return io.NopCloser(strings.NewReader(s)) }
