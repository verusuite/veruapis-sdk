// Package veruapis is a client for the VeruSuite API.
//
// Every call is authorised by a key created at veruapis.com and acts as the
// person who created it, limited to the permissions they granted. The key is
// never widened by anything here.
//
//	api := veruapis.New(os.Getenv("VERUAPIS_KEY"))
//
//	folders, err := api.Mail.ListFolders(ctx)
//	if err != nil {
//		var apiErr *veruapis.Error
//		if errors.As(err, &apiErr) && apiErr.IsPermissionProblem() {
//			log.Fatalf("this key is missing %s", apiErr.Code)
//		}
//	}
//
// Written by hand, types included, and checked against the API's own
// description by a test rather than generated from it. A generator produces
// types you reach through rather than types you use, and the surface that
// matters here is small: one envelope, one error shape, cursor paging.
//
// The client has no dependencies outside the standard library. That is worth
// more in a library than anywhere else: every dependency here becomes one in
// every program that imports it.
package veruapis

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"math/rand"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

// DefaultBaseURL is the production API.
const DefaultBaseURL = "https://api.veruapis.com"

// Client calls the API. Safe for concurrent use.
type Client struct {
	// Mail is folders, messages, drafts and sending.
	Mail *MailService

	// Calendar is calendars, events and availability.
	Calendar *CalendarService

	apiKey     string
	baseURL    string
	httpClient *http.Client
	maxRetries int

	// now and sleep exist so the retry behaviour can be tested without the
	// test taking as long as the backoff it is asserting.
	sleep func(context.Context, time.Duration) error
}

// Option configures a Client.
type Option func(*Client)

// WithBaseURL points the client somewhere other than production.
func WithBaseURL(u string) Option {
	return func(c *Client) { c.baseURL = strings.TrimRight(u, "/") }
}

// WithHTTPClient supplies the http.Client to use.
//
// Worth setting in any program that already has one: connection pooling,
// proxies and timeouts are usually decided once for a whole process rather
// than per library.
func WithHTTPClient(h *http.Client) Option {
	return func(c *Client) { c.httpClient = h }
}

// WithMaxRetries sets how many times a retryable failure is tried again.
//
// Only 429, 408 and 5xx are ever retried, and only after the delay the server
// asked for. Zero disables retrying entirely.
func WithMaxRetries(n int) Option {
	return func(c *Client) {
		if n >= 0 {
			c.maxRetries = n
		}
	}
}

// New builds a client for the given key.
func New(apiKey string, options ...Option) *Client {
	c := &Client{
		apiKey:     apiKey,
		baseURL:    DefaultBaseURL,
		httpClient: &http.Client{Timeout: 30 * time.Second},
		maxRetries: 2,
		sleep:      sleepContext,
	}

	for _, option := range options {
		option(c)
	}

	c.Mail = &MailService{client: c}
	c.Calendar = &CalendarService{client: c}
	return c
}

// Request is one call to the API.
type Request struct {
	Method string
	Path   string
	Query  url.Values

	// Body is marshalled as JSON when not nil.
	Body any

	// IdempotencyKey makes a retried write safe: the server returns the first
	// response rather than doing the work twice. One is generated for every
	// write when this is empty, because a retry that sends two emails is the
	// failure people actually hit.
	IdempotencyKey string
}

// Envelope is the shape every response arrives in.
type Envelope[T any] struct {
	Data T    `json:"data"`
	Meta Meta `json:"meta"`
}

// Meta accompanies every response.
type Meta struct {
	// RequestID identifies this call. Quote it when reporting a problem.
	RequestID string `json:"request_id"`
	Timestamp string `json:"timestamp"`

	// NextCursor is present on a list that has another page.
	NextCursor string `json:"next_cursor,omitempty"`
}

// Do performs a request and decodes the envelope's data into out.
//
// A generic function rather than a method, because Go has no generic methods
// and the alternative is decoding into any and asserting at every call site.
func Do[T any](ctx context.Context, c *Client, req Request) (T, Meta, error) {
	var zero T

	envelope, err := doEnvelope[T](ctx, c, req)
	if err != nil {
		return zero, Meta{}, err
	}
	return envelope.Data, envelope.Meta, nil
}

func doEnvelope[T any](ctx context.Context, c *Client, req Request) (Envelope[T], error) {
	var out Envelope[T]

	if c.apiKey == "" {
		return out, fmt.Errorf("veruapis: no API key. Create one at veruapis.com")
	}

	target := c.baseURL + req.Path
	if len(req.Query) > 0 {
		target += "?" + req.Query.Encode()
	}

	var payload []byte
	if req.Body != nil {
		encoded, err := json.Marshal(req.Body)
		if err != nil {
			return out, fmt.Errorf("veruapis: encode body: %w", err)
		}
		payload = encoded
	}

	// One key for every attempt, not one per attempt: a retry that generated a
	// new key would be a second request as far as the server is concerned,
	// which defeats the point of sending one.
	idempotencyKey := req.IdempotencyKey
	if idempotencyKey == "" && req.Method != http.MethodGet && req.Method != http.MethodDelete {
		idempotencyKey = newIdempotencyKey()
	}

	var lastErr error

	for attempt := 0; ; attempt++ {
		var body io.Reader
		if payload != nil {
			// A fresh reader per attempt: the previous one is consumed.
			body = bytes.NewReader(payload)
		}

		httpReq, err := http.NewRequestWithContext(ctx, req.Method, target, body)
		if err != nil {
			return out, fmt.Errorf("veruapis: build request: %w", err)
		}

		httpReq.Header.Set("Authorization", "Bearer "+c.apiKey)
		httpReq.Header.Set("Accept", "application/json")
		if payload != nil {
			httpReq.Header.Set("Content-Type", "application/json")
		}
		if idempotencyKey != "" {
			httpReq.Header.Set("Idempotency-Key", idempotencyKey)
		}

		res, err := c.httpClient.Do(httpReq)
		if err != nil {
			// A transport failure. Worth another try unless the caller's
			// context has gone, in which case nothing will help.
			lastErr = fmt.Errorf("veruapis: %w", err)
			if attempt >= c.maxRetries || ctx.Err() != nil {
				return out, lastErr
			}
			if waitErr := c.sleep(ctx, backoff(attempt)); waitErr != nil {
				return out, waitErr
			}
			continue
		}

		decoded, apiErr, err := readResponse[T](res)
		if err != nil {
			return out, err
		}
		if apiErr == nil {
			return decoded, nil
		}

		// Retried only when the server said to. A 400 or a 403 fails the same
		// way however many times it is sent.
		if !apiErr.retryable() || attempt >= c.maxRetries {
			return out, apiErr
		}

		wait := apiErr.RetryAfter
		if wait <= 0 {
			wait = backoff(attempt)
		}
		if waitErr := c.sleep(ctx, wait); waitErr != nil {
			return out, waitErr
		}
		lastErr = apiErr
	}
}

// readResponse turns one HTTP response into a decoded envelope or an API error.
func readResponse[T any](res *http.Response) (Envelope[T], *Error, error) {
	var out Envelope[T]
	defer res.Body.Close()

	// Bounded: a client library should not be the reason a program runs out of
	// memory because something upstream answered with a stream.
	raw, err := io.ReadAll(io.LimitReader(res.Body, 32<<20))
	if err != nil {
		return out, nil, fmt.Errorf("veruapis: read response: %w", err)
	}

	if res.StatusCode >= 400 {
		return out, newError(res, raw), nil
	}

	// 204 has no body to decode, and json.Unmarshal on nothing is an error
	// rather than an empty value.
	if res.StatusCode == http.StatusNoContent || len(raw) == 0 {
		return out, nil, nil
	}

	if err := json.Unmarshal(raw, &out); err != nil {
		return out, nil, fmt.Errorf("veruapis: decode response: %w", err)
	}
	return out, nil, nil
}

// backoff is exponential with jitter, so a fleet of clients does not retry in
// step and turn a brief failure into a sustained one.
func backoff(attempt int) time.Duration {
	base := time.Duration(1<<uint(attempt)) * 500 * time.Millisecond
	if base > 8*time.Second {
		base = 8 * time.Second
	}
	return base/2 + time.Duration(rand.Int63n(int64(base/2)+1))
}

// sleepContext waits, or gives up when the caller does.
func sleepContext(ctx context.Context, d time.Duration) error {
	timer := time.NewTimer(d)
	defer timer.Stop()

	select {
	case <-ctx.Done():
		return ctx.Err()
	case <-timer.C:
		return nil
	}
}

// newIdempotencyKey returns a random value for one write.
//
// Not a UUID library: this needs to be unlikely to repeat, not to be a
// conforming UUID, and a dependency in a client library is one in every program
// that imports it.
func newIdempotencyKey() string {
	var b [16]byte
	for i := range b {
		b[i] = byte(rand.Intn(256))
	}
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// query builds a query string, skipping empty values and repeating a list
// rather than joining it, which the API would read as one value.
func query(pairs map[string]any) url.Values {
	out := url.Values{}

	for key, value := range pairs {
		switch v := value.(type) {
		case nil:
		case string:
			if v != "" {
				out.Set(key, v)
			}
		case []string:
			for _, item := range v {
				out.Add(key, item)
			}
		case bool:
			if v {
				out.Set(key, "true")
			}
		case int:
			if v != 0 {
				out.Set(key, strconv.Itoa(v))
			}
		default:
			out.Set(key, fmt.Sprint(v))
		}
	}
	return out
}
