import type { Transport } from "./client.js";
import type {
  Calendar,
  Event,
  Folder,
  FreeBusy,
  ListEventsQuery,
  ListMessagesQuery,
  Message,
  MessageSummary,
  PageQuery,
  SendMessage,
} from "./types.js";

/**
 * The methods, grouped the way the API is.
 *
 * `api.mail.listFolders()` rather than `api.request("GET", "/v1/folders")`.
 * The raw form is still there for an endpoint this file has not got to yet, but
 * it should not be what anybody reaches for first: a route is a detail of how
 * the call is made, and a caller is thinking about mail.
 */

export class Mail {
  constructor(private readonly t: Transport) {}

  /** Every folder, with its message and unread counts. Not paged: a mailbox has tens. */
  listFolders(): Promise<Folder[]> {
    return this.t.request("GET", "/v1/folders");
  }

  /**
   * One page of messages, newest first.
   *
   * At least one filter is required. An unfiltered list would be the whole
   * mailbox, so the API refuses it rather than quietly returning the inbox.
   */
  listMessages(query: ListMessagesQuery): Promise<MessageSummary[]> {
    return this.t.request("GET", "/v1/messages", { query });
  }

  /** Every message matching the filter, walking the pages for you. */
  messages(query: ListMessagesQuery): AsyncGenerator<MessageSummary> {
    return this.t.paginate<MessageSummary>("/v1/messages", { query });
  }

  /** One message, with headers and body decoded. */
  getMessage(id: string): Promise<Message> {
    return this.t.request("GET", `/v1/messages/${encodeURIComponent(id)}`);
  }

  /**
   * Sends mail as the key's owner.
   *
   * Answers 202: the message is queued and archived in Sent, and delivery
   * happens afterwards. That is not a promise it arrived, and this API cannot
   * tell you that it did. A failure comes back later as a bounce.
   */
  send(message: SendMessage, idempotencyKey?: string): Promise<{ id: string }> {
    return this.t.request("POST", "/v1/messages/send", { body: message, idempotencyKey });
  }

  /** The attachment list for a message. Bytes are a separate call. */
  listAttachments(messageId: string): Promise<Message["attachments"]> {
    return this.t.request("GET", `/v1/messages/${encodeURIComponent(messageId)}/attachments`);
  }

  listDrafts(query: PageQuery = {}): Promise<MessageSummary[]> {
    return this.t.request("GET", "/v1/drafts", { query });
  }
}

export class CalendarApi {
  constructor(private readonly t: Transport) {}

  listCalendars(): Promise<Calendar[]> {
    return this.t.request("GET", "/v1/calendars");
  }

  getCalendar(id: string): Promise<Calendar> {
    return this.t.request("GET", `/v1/calendars/${encodeURIComponent(id)}`);
  }

  /**
   * Events across every calendar in a window.
   *
   * Recurring events arrive already expanded, one entry per occurrence, which
   * is why the window is required rather than optional.
   */
  listEvents(query: ListEventsQuery): Promise<Event[]> {
    return this.t.request("GET", "/v1/events", { query });
  }

  /** Every event in the window, walking the pages for you. */
  events(query: ListEventsQuery): AsyncGenerator<Event> {
    return this.t.paginate<Event>("/v1/events", { query });
  }

  getEvent(calendarId: string, id: string): Promise<Event> {
    return this.t.request(
      "GET",
      `/v1/calendars/${encodeURIComponent(calendarId)}/events/${encodeURIComponent(id)}`
    );
  }

  /** Times are UTC. Anything scheduling by wall clock needs the user's zone from elsewhere. */
  createEvent(
    calendarId: string,
    event: Partial<Event> & Pick<Event, "summary" | "starts_at" | "ends_at">,
    idempotencyKey?: string
  ): Promise<Event> {
    return this.t.request("POST", `/v1/calendars/${encodeURIComponent(calendarId)}/events`, {
      body: event,
      idempotencyKey,
    });
  }

  updateEvent(calendarId: string, id: string, changes: Partial<Event>): Promise<Event> {
    return this.t.request(
      "PATCH",
      `/v1/calendars/${encodeURIComponent(calendarId)}/events/${encodeURIComponent(id)}`,
      { body: changes }
    );
  }

  /**
   * Cancels an event, which notifies the attendees.
   *
   * Not the same as deleting it, which does not. Use the one that matches what
   * actually happened.
   */
  deleteEvent(calendarId: string, id: string): Promise<void> {
    return this.t.request(
      "DELETE",
      `/v1/calendars/${encodeURIComponent(calendarId)}/events/${encodeURIComponent(id)}`
    );
  }

  /**
   * When these people are busy, without reading their events.
   *
   * The right call for availability: it returns windows rather than detail, so
   * it needs far less access than reading everyone's calendar to work it out.
   */
  freeBusy(emails: string[], start: string, end: string): Promise<FreeBusy[]> {
    return this.t.request("POST", "/v1/freebusy", { body: { emails, start, end } });
  }
}
