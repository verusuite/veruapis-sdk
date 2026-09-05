package veruapis

import (
	"encoding/json"
	"fmt"
	"net/http"
	"strconv"
	"time"
)

// Error is a refusal from the API.
//
// Reached with errors.As, so a caller can branch on it without knowing how the
// client wrapped it:
//
//	var apiErr *veruapis.Error
//	if errors.As(err, &apiErr) && apiErr.IsPermissionProblem() {
//		...
//	}
//
// Branch on Code, never on Message. The code is a stable identifier; the
// message is written for a person and may be reworded at any time.
type Error struct {
	// Code identifies the failure. This is the field to switch on.
	Code string

	// Message is written for a person. Do not parse it.
	Message string

	// Status is the HTTP status the API answered with.
	Status int

	// RequestID identifies the call. Quote it when reporting a problem.
	RequestID string

	// Details names the fields that were wrong, when that is the reason.
	Details []ErrorDetail

	// RetryAfter is how long the API asked the caller to wait, when it said.
	// Zero when it did not.
	RetryAfter time.Duration
}

// ErrorDetail is one field the API rejected.
type ErrorDetail struct {
	Field   string `json:"field"`
	Message string `json:"message,omitempty"`
}

func (e *Error) Error() string {
	if e.RequestID != "" {
		return fmt.Sprintf("veruapis: %s (%s, request %s)", e.Message, e.Code, e.RequestID)
	}
	return fmt.Sprintf("veruapis: %s (%s)", e.Message, e.Code)
}

// IsPermissionProblem reports whether the key is valid but was not granted a
// permission this call needs. Creating a new key with the right permission is
// the fix; retrying is not.
func (e *Error) IsPermissionProblem() bool { return e.Status == http.StatusForbidden }

// IsRateLimited reports whether the plan's rate limit or daily quota is spent.
// The client already waits and retries; this is for a caller that wants to
// slow itself down rather than be slowed.
func (e *Error) IsRateLimited() bool { return e.Status == http.StatusTooManyRequests }

// IsNotFound reports whether the record does not exist, or belongs to another
// workspace. The API does not distinguish the two, deliberately.
func (e *Error) IsNotFound() bool { return e.Status == http.StatusNotFound }

// IsAuthProblem reports whether the credential was missing, malformed, or not
// one this API issues.
func (e *Error) IsAuthProblem() bool { return e.Status == http.StatusUnauthorized }

// retryable reports whether sending the same request again could succeed.
func (e *Error) retryable() bool {
	return e.Status == http.StatusTooManyRequests ||
		e.Status == http.StatusRequestTimeout ||
		e.Status >= 500
}

// newError reads the API's error envelope out of a failed response.
func newError(res *http.Response, body []byte) *Error {
	out := &Error{
		Status:     res.StatusCode,
		RetryAfter: retryAfter(res),
	}

	var envelope struct {
		Error struct {
			Code      string        `json:"code"`
			Message   string        `json:"message"`
			RequestID string        `json:"request_id"`
			Details   []ErrorDetail `json:"details"`
		} `json:"error"`
	}

	if err := json.Unmarshal(body, &envelope); err != nil || envelope.Error.Code == "" {
		// Something upstream of the API answered: a proxy, an error page, a
		// misrouted request. Saying so beats reporting a parse failure nobody
		// can act on.
		out.Code = "unreadable_response"
		out.Message = fmt.Sprintf("the server answered %d", res.StatusCode)
		return out
	}

	out.Code = envelope.Error.Code
	out.Message = envelope.Error.Message
	out.RequestID = envelope.Error.RequestID
	out.Details = envelope.Error.Details
	return out
}

// retryAfter reads the header, which the RFC allows as seconds or as a date.
func retryAfter(res *http.Response) time.Duration {
	header := res.Header.Get("Retry-After")
	if header == "" {
		return 0
	}

	if seconds, err := strconv.Atoi(header); err == nil {
		if seconds < 0 {
			return 0
		}
		return time.Duration(seconds) * time.Second
	}

	when, err := http.ParseTime(header)
	if err != nil {
		return 0
	}
	if wait := time.Until(when); wait > 0 {
		return wait
	}
	return 0
}
