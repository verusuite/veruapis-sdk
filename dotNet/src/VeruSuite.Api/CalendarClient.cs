namespace VeruSuite.Api;

/// <summary>Calendars, events and availability.</summary>
public sealed class CalendarClient
{
    private readonly VeruApiClient _api;

    internal CalendarClient(VeruApiClient api) => _api = api;

    /// <summary>The calendars this user can see.</summary>
    public async Task<IReadOnlyList<Calendar>> ListCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<Calendar>>(
            HttpMethod.Get, "/v1/calendars", cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<Calendar>();
    }

    /// <summary>One calendar.</summary>
    public async Task<Calendar?> GetCalendarAsync(string calendarId, CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Calendar>(
            HttpMethod.Get, "/v1/calendars/" + VeruApiClient.Escape(calendarId), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// One page of events across every calendar in a window.
    /// </summary>
    /// <remarks>
    /// Recurring events arrive already expanded, one entry per occurrence,
    /// which is why the window is required rather than optional: a series
    /// expands without limit, so there is no finite answer to "all events".
    /// </remarks>
    public async Task<(IReadOnlyList<Event> Events, Meta Meta)> ListEventsAsync(
        ListEventsQuery query,
        CancellationToken cancellationToken = default)
    {
        var (data, meta) = await _api.SendAsync<List<Event>>(
            HttpMethod.Get, "/v1/events", EventQuery(query), cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (data ?? new List<Event>(), meta);
    }

    /// <summary>Every event in the window, following the cursor.</summary>
    public IAsyncEnumerable<Event> EventsAsync(
        ListEventsQuery query,
        CancellationToken cancellationToken = default) =>
        _api.PaginateAsync<Event>("/v1/events", EventQuery(query), cancellationToken);

    /// <summary>One event.</summary>
    public async Task<Event?> GetEventAsync(
        string calendarId,
        string eventId,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Event>(
            HttpMethod.Get,
            $"/v1/calendars/{VeruApiClient.Escape(calendarId)}/events/{VeruApiClient.Escape(eventId)}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// Adds an event to a calendar.
    /// </summary>
    /// <remarks>
    /// Times are UTC. An Idempotency-Key is generated unless one is supplied,
    /// so a retried booking cannot create a second event.
    /// </remarks>
    public async Task<Event?> CreateEventAsync(
        string calendarId,
        Event body,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Event>(
            HttpMethod.Post,
            $"/v1/calendars/{VeruApiClient.Escape(calendarId)}/events",
            body: body,
            idempotencyKey: idempotencyKey,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data;
    }

    /// <summary>Amends an event. Only the fields set are changed.</summary>
    public async Task<Event?> UpdateEventAsync(
        string calendarId,
        string eventId,
        Event changes,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<Event>(
            HttpMethod.Patch,
            $"/v1/calendars/{VeruApiClient.Escape(calendarId)}/events/{VeruApiClient.Escape(eventId)}",
            body: changes,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data;
    }

    /// <summary>
    /// Removes an event.
    /// </summary>
    /// <remarks>
    /// Cancelling notifies the attendees; this does not. Use the one that
    /// matches what actually happened.
    /// </remarks>
    public Task DeleteEventAsync(
        string calendarId,
        string eventId,
        CancellationToken cancellationToken = default) =>
        _api.SendAsync<object>(
            HttpMethod.Delete,
            $"/v1/calendars/{VeruApiClient.Escape(calendarId)}/events/{VeruApiClient.Escape(eventId)}",
            cancellationToken: cancellationToken);

    /// <summary>
    /// When these people are busy, without returning event detail.
    /// </summary>
    /// <remarks>
    /// The right call for availability: it needs far less access than reading
    /// everybody's calendar to work the same thing out.
    /// </remarks>
    public async Task<IReadOnlyList<FreeBusy>> FreeBusyAsync(
        IReadOnlyList<string> emails,
        string start,
        string end,
        CancellationToken cancellationToken = default)
    {
        var (data, _) = await _api.SendAsync<List<FreeBusy>>(
            HttpMethod.Post,
            "/v1/freebusy",
            body: new Dictionary<string, object> { ["emails"] = emails, ["start"] = start, ["end"] = end },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return data ?? new List<FreeBusy>();
    }

    private static Dictionary<string, object?> EventQuery(ListEventsQuery query) => new()
    {
        ["start"] = query.Start,
        ["end"] = query.End,
        ["calendar_id"] = query.CalendarId,
        ["limit"] = query.Limit,
        ["cursor"] = query.Cursor,
    };
}
