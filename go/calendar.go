package veruapis

import (
	"context"
	"net/http"
	"net/url"
)

// CalendarService is calendars, events and availability.
type CalendarService struct{ client *Client }

// ListCalendars returns the calendars this user can see.
func (s *CalendarService) ListCalendars(ctx context.Context) ([]Calendar, error) {
	out, _, err := Do[[]Calendar](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/calendars",
	})
	return out, err
}

// GetCalendar returns one calendar.
func (s *CalendarService) GetCalendar(ctx context.Context, id string) (Calendar, error) {
	out, _, err := Do[Calendar](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/calendars/" + url.PathEscape(id),
	})
	return out, err
}

// ListEvents returns one page of events across every calendar in a window.
//
// Recurring events arrive already expanded, one entry per occurrence, which is
// why the window is required rather than optional.
func (s *CalendarService) ListEvents(ctx context.Context, opts ListEventsOptions) ([]Event, Meta, error) {
	return Do[[]Event](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/events",
		Query:  opts.query(),
	})
}

// Events iterates every event in the window, following the cursor.
func (s *CalendarService) Events(ctx context.Context, opts ListEventsOptions) Seq2[Event] {
	return paginate[Event](ctx, s.client, "/v1/events", opts.query())
}

// GetEvent returns one event.
func (s *CalendarService) GetEvent(ctx context.Context, calendarID, id string) (Event, error) {
	out, _, err := Do[Event](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/calendars/" + url.PathEscape(calendarID) + "/events/" + url.PathEscape(id),
	})
	return out, err
}

// CreateEvent adds an event to a calendar.
//
// Times are UTC. An Idempotency-Key is generated unless one is supplied, so a
// retried booking cannot create a second event.
func (s *CalendarService) CreateEvent(ctx context.Context, calendarID string, event Event, idempotencyKey ...string) (Event, error) {
	req := Request{
		Method: http.MethodPost,
		Path:   "/v1/calendars/" + url.PathEscape(calendarID) + "/events",
		Body:   event,
	}
	if len(idempotencyKey) > 0 {
		req.IdempotencyKey = idempotencyKey[0]
	}

	out, _, err := Do[Event](ctx, s.client, req)
	return out, err
}

// UpdateEvent amends an event. Only the fields set are changed.
func (s *CalendarService) UpdateEvent(ctx context.Context, calendarID, id string, changes Event) (Event, error) {
	out, _, err := Do[Event](ctx, s.client, Request{
		Method: http.MethodPatch,
		Path:   "/v1/calendars/" + url.PathEscape(calendarID) + "/events/" + url.PathEscape(id),
		Body:   changes,
	})
	return out, err
}

// DeleteEvent removes an event.
//
// Cancelling notifies the attendees; this does not. Use the one that matches
// what actually happened.
func (s *CalendarService) DeleteEvent(ctx context.Context, calendarID, id string) error {
	_, _, err := Do[struct{}](ctx, s.client, Request{
		Method: http.MethodDelete,
		Path:   "/v1/calendars/" + url.PathEscape(calendarID) + "/events/" + url.PathEscape(id),
	})
	return err
}

// FreeBusy reports when these people are busy, without returning event detail.
//
// The right call for availability: it needs far less access than reading
// everybody's calendar to work the same thing out.
func (s *CalendarService) FreeBusy(ctx context.Context, emails []string, start, end string) ([]FreeBusy, error) {
	out, _, err := Do[[]FreeBusy](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/freebusy",
		Body: map[string]any{
			"emails": emails,
			"start":  start,
			"end":    end,
		},
	})
	return out, err
}

// query renders the filter.
func (o ListEventsOptions) query() url.Values {
	return query(map[string]any{
		"start":       o.Start,
		"end":         o.End,
		"calendar_id": o.CalendarID,
		"limit":       o.Limit,
		"cursor":      o.Cursor,
	})
}
