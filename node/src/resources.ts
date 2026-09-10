import type { Transport } from "./client.js";
import type {
  AddressBook,
  Calendar,
  Cell,
  Comment,
  Contact,
  Document,
  DocumentState,
  File,
  FileContent,
  FileFolder,
  Group,
  ListContactsQuery,
  ListDocumentsQuery,
  ListFilesQuery,
  NewUpload,
  Permission,
  Profile,
  UploadedPart,
  UploadSession,
  Event,
  Folder,
  FreeBusy,
  ListEventsQuery,
  ListMessagesQuery,
  Message,
  MessageSummary,
  PageQuery,
  SendMessage,
  Sheet,
  SheetRequest,
  StructureResult,
  ValueRange,
  ValueRender,
  Workbook,
  WriteResult,
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

/**
 * The contents of a spreadsheet.
 *
 * A spreadsheet's lifecycle — creating, renaming, trashing — is Documents,
 * because a spreadsheet is a document. These are its cells.
 *
 * Worth knowing before granting a key: writing values needs
 * `sheets.values.write` and merges cleanly with whatever else is happening in
 * the document, while inserting or deleting rows needs
 * `sheets.structure.write` and moves everything below it.
 */
export class Spreadsheets {
  constructor(private readonly t: Transport) {}

  /**
   * The workbook: every sheet with its extent and merges.
   *
   * This is how you list the tabs; there is no separate call for them. Read it
   * first to learn the sheet names an A1 range needs.
   */
  get(spreadsheetId: string): Promise<Workbook> {
    return this.t.request("GET", `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}`);
  }

  /**
   * The document's change token.
   *
   * Two uses, one value: poll it to notice somebody else's change — there are
   * no webhooks for spreadsheets — and pass it to `applyStructure`, which
   * refuses to work without it.
   */
  state(spreadsheetId: string): Promise<DocumentState> {
    return this.t.request("GET", `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}/state`);
  }

  /** Reads one A1 range. */
  values(spreadsheetId: string, range: string, render?: ValueRender): Promise<ValueRange> {
    return this.t.request("GET", valuesPath(spreadsheetId, range), {
      query: renderQuery(render),
    });
  }

  /**
   * Reads several ranges in one call.
   *
   * The cell cap applies to the whole call, and a computed read evaluates the
   * workbook once for all of them rather than once per range.
   */
  async batchValues(
    spreadsheetId: string,
    ranges: string[],
    render?: ValueRender
  ): Promise<ValueRange[]> {
    const out = await this.t.request<{ value_ranges: ValueRange[] }>(
      "POST",
      `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}/values/batch-get`,
      { query: renderQuery(render), body: { ranges } }
    );
    return out.value_ranges;
  }

  /**
   * Overwrites one range.
   *
   * Values are anchored at its top-left corner, and a block shorter than the
   * range leaves the rest untouched: writing two rows into a ten-row range
   * writes two rows. Clearing is a separate call for that reason.
   *
   * An empty string deletes a cell rather than storing a blank, which would
   * keep it inside the sheet's used extent and widen every unbounded range from
   * then on.
   */
  write(spreadsheetId: string, range: string, values: Cell[][]): Promise<WriteResult> {
    return this.t.request("PUT", valuesPath(spreadsheetId, range), { body: { values } });
  }

  /**
   * Writes several ranges as one document write, so somebody with the sheet
   * open sees one change rather than a flicker of several.
   */
  batchWrite(spreadsheetId: string, data: ValueRange[]): Promise<WriteResult> {
    return this.t.request(
      "POST",
      `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}/values/batch-update`,
      { body: { data } }
    );
  }

  /**
   * Adds rows after the last populated row of the range's own columns.
   *
   * Of its own columns, not the sheet's: an unrelated note in column Z does not
   * push your table down. This is the call a log or a nightly export wants — it
   * needs no read first, and two appends cannot overwrite each other.
   */
  append(spreadsheetId: string, range: string, values: Cell[][]): Promise<WriteResult> {
    return this.t.request("POST", `${valuesPath(spreadsheetId, range)}/append`, {
      body: { values },
    });
  }

  /** Empties a range and leaves its formatting, so a template keeps its headers and formats. */
  clear(spreadsheetId: string, range: string): Promise<WriteResult> {
    return this.t.request("POST", `${valuesPath(spreadsheetId, range)}/clear`);
  }

  /**
   * Inserts and deletes rows, columns and sheets, and changes merges, sorting
   * and formatting.
   *
   * `state` comes from `state()` and is required: these are the changes that
   * move data other requests address by position, so one applied to a document
   * that has moved on merges cleanly into a corrupt grid. A stale token is
   * refused with a conflict — read the state again and reapply.
   *
   * Requests apply in order and each sees the effect of the one before, so
   * adding a sheet and formatting it in one batch works.
   */
  applyStructure(
    spreadsheetId: string,
    state: string,
    requests: SheetRequest[]
  ): Promise<StructureResult> {
    return this.t.request(
      "POST",
      `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}/batch-update`,
      { body: { requests }, ifMatch: state }
    );
  }
}

/**
 * A range is escaped as one segment.
 *
 * A1 notation carries characters a URL reads as structure — a quoted sheet
 * name, a space, a colon — and a range that split into two segments would
 * address a route that does not exist.
 */
function valuesPath(spreadsheetId: string, range: string): string {
  return `/v1/spreadsheets/${encodeURIComponent(spreadsheetId)}/values/${encodeURIComponent(range)}`;
}

function renderQuery(render?: ValueRender): Record<string, string> | undefined {
  return render && render !== "stored" ? { value_render: render } : undefined;
}

/** Who the key acts as, and the people around them. */
export class Identity {
  constructor(private readonly t: Transport) {}

  /**
   * The profile of the user this key acts as.
   *
   * The cheapest way to check a key works: one call, and it needs only
   * identity.read.
   */
  me(): Promise<Profile> {
    return this.t.request("GET", "/v1/me");
  }

  /** One page of the workspace's groups, ordered by name. */
  listGroups(query: PageQuery = {}): Promise<Group[]> {
    return this.t.request("GET", "/v1/groups", { query });
  }

  /** Every group, walking the pages. */
  groups(query: PageQuery = {}): AsyncGenerator<Group> {
    return this.t.paginate<Group>("/v1/groups", { query });
  }
}

/** Address books and the people in them. */
export class Contacts {
  constructor(private readonly t: Transport) {}

  /** Every book in the mailbox. Not paged: there are a handful. */
  listAddressBooks(): Promise<AddressBook[]> {
    return this.t.request("GET", "/v1/address-books");
  }

  /** One page of contacts. */
  listContacts(query: ListContactsQuery = {}): Promise<Contact[]> {
    return this.t.request("GET", "/v1/contacts", { query });
  }

  /** Every contact matching the filter, walking the pages. */
  contacts(query: ListContactsQuery = {}): AsyncGenerator<Contact> {
    return this.t.paginate<Contact>("/v1/contacts", { query });
  }

  /** One contact. */
  getContact(id: string): Promise<Contact> {
    return this.t.request("GET", `/v1/contacts/${encodeURIComponent(id)}`);
  }

  /**
   * Adds a contact to an address book.
   *
   * `name` is the only required field. A field this API does not publish is
   * refused rather than ignored, so a typo is a 400 here rather than a value
   * that silently never arrived.
   */
  createContact(contact: Contact): Promise<Contact> {
    return this.t.request("POST", "/v1/contacts", { body: contact });
  }

  /**
   * Edits a contact.
   *
   * Fields left out are left alone, but a list that is sent replaces the whole
   * list rather than adding to it: read the contact first if you mean to append
   * an address.
   */
  updateContact(id: string, contact: Contact): Promise<Contact> {
    return this.t.request("PATCH", `/v1/contacts/${encodeURIComponent(id)}`, { body: contact });
  }

  /** Removes a contact from its address book. */
  deleteContact(id: string): Promise<void> {
    return this.t.request("DELETE", `/v1/contacts/${encodeURIComponent(id)}`);
  }
}

/**
 * Documents and spreadsheets, their comments, and who they are shared with.
 *
 * An uploaded file is not here: /v1/documents is what somebody authored and
 * /v1/files is what somebody uploaded, so a listing never begins with a filter
 * and asking the wrong collection for an id answers 404 rather than something
 * that half fits.
 */
export class Documents {
  constructor(private readonly t: Transport) {}

  /** One page of documents and spreadsheets. */
  listDocuments(query: ListDocumentsQuery = {}): Promise<Document[]> {
    return this.t.request("GET", "/v1/documents", { query });
  }

  /**
   * Every document matching the filter, walking the pages.
   *
   * This is how an automation finds the spreadsheet id every `spreadsheets`
   * call needs, rather than having somebody paste one out of a browser.
   */
  documents(query: ListDocumentsQuery = {}): AsyncGenerator<Document> {
    return this.t.paginate<Document>("/v1/documents", { query });
  }

  /** One document's metadata. Contents are read through `spreadsheets`. */
  getDocument(id: string): Promise<Document> {
    return this.t.request("GET", `/v1/documents/${encodeURIComponent(id)}`);
  }

  /**
   * Creates a document or a spreadsheet.
   *
   * A new spreadsheet arrives with its workbook seeded, so a range can be
   * written into it in the very next call.
   */
  createDocument(document: Document): Promise<Document> {
    return this.t.request("POST", "/v1/documents", { body: document });
  }

  /**
   * Renames a document or changes its metadata.
   *
   * Anyone with it open is told, so a title changed here appears in their tab
   * without a reload. `metadata` replaces the stored object rather than merging
   * into it.
   */
  updateDocument(id: string, document: Document): Promise<Document> {
    return this.t.request("PATCH", `/v1/documents/${encodeURIComponent(id)}`, { body: document });
  }

  /**
   * Moves a document to the trash.
   *
   * Reversible with `restoreDocument`. A permanent delete is not exposed at
   * all, so a job retrying a failed batch cannot destroy somebody's work.
   */
  deleteDocument(id: string): Promise<void> {
    return this.t.request("DELETE", `/v1/documents/${encodeURIComponent(id)}`);
  }

  /**
   * Takes a document back out of the trash.
   *
   * Restoring one that was never trashed changes nothing and still answers with
   * it, so a retry is safe.
   */
  restoreDocument(id: string): Promise<Document> {
    return this.t.request("POST", `/v1/documents/${encodeURIComponent(id)}/restore`);
  }

  /**
   * Copies a document with its contents.
   *
   * Both fields are optional: an empty object copies into the root as "Copy of"
   * the original. The copy belongs to the key's owner.
   */
  copyDocument(id: string, copy: Pick<Document, "title" | "folder_id"> = {}): Promise<Document> {
    return this.t.request("POST", `/v1/documents/${encodeURIComponent(id)}/copy`, { body: copy });
  }

  /**
   * The comment threads on a document.
   *
   * Resolved threads are included unless you say otherwise, which is the
   * opposite of the editor's sidebar: an integration auditing a document wants
   * the whole history.
   */
  listComments(documentId: string, includeResolved = true): Promise<Comment[]> {
    return this.t.request("GET", `/v1/documents/${encodeURIComponent(documentId)}/comments`, {
      query: includeResolved ? undefined : { include_resolved: "false" },
    });
  }

  /** Posts a comment, or a reply when `parent_id` is set. */
  createComment(documentId: string, comment: Comment): Promise<Comment> {
    return this.t.request("POST", `/v1/documents/${encodeURIComponent(documentId)}/comments`, {
      body: comment,
    });
  }

  /**
   * Edits a comment's text or resolves the thread.
   *
   * The two are not the same permission: anyone who may comment can resolve or
   * reopen a thread, while editing the words is the author's alone.
   */
  updateComment(commentId: string, comment: Comment): Promise<Comment> {
    return this.t.request("PATCH", `/v1/comments/${encodeURIComponent(commentId)}`, {
      body: comment,
    });
  }

  /** Removes a comment. Only its author may. */
  deleteComment(commentId: string): Promise<void> {
    return this.t.request("DELETE", `/v1/comments/${encodeURIComponent(commentId)}`);
  }
}

/**
 * Uploaded files, their folders, sharing, and resumable upload.
 *
 * The folders are file-folders rather than folders because /v1/folders is
 * mail's. Two products, one word, and the API spells the less obvious one out
 * rather than leaving a caller to guess which listing they are reading.
 */
export class Files {
  constructor(private readonly t: Transport) {}

  /**
   * The drive's folders, or its trash.
   *
   * A flat list with parent ids rather than a nested tree, so you build
   * whichever shape you need rather than this API deciding the depth.
   */
  listFolders(trashed = false): Promise<FileFolder[]> {
    return this.t.request("GET", "/v1/file-folders", {
      query: trashed ? { trashed: "true" } : undefined,
    });
  }

  /** Makes a folder, at the root unless `parent_id` says otherwise. */
  createFolder(folder: FileFolder): Promise<FileFolder> {
    return this.t.request("POST", "/v1/file-folders", { body: folder });
  }

  /**
   * Renames a folder, moves it, or both in that order.
   *
   * An explicit empty `parent_id` moves it to the root; leaving the field out
   * leaves it where it is. The two are different instructions and JSON cannot
   * tell an absent value from a null one, which is why this is spelled out.
   */
  updateFolder(id: string, changes: Pick<FileFolder, "name" | "parent_id">): Promise<FileFolder> {
    return this.t.request("PATCH", `/v1/file-folders/${encodeURIComponent(id)}`, {
      body: changes,
    });
  }

  /** Moves a folder to the trash. */
  deleteFolder(id: string): Promise<void> {
    return this.t.request("DELETE", `/v1/file-folders/${encodeURIComponent(id)}`);
  }

  /** One page of uploaded files. */
  listFiles(query: ListFilesQuery = {}): Promise<File[]> {
    return this.t.request("GET", "/v1/files", { query });
  }

  /** Every file matching the filter, walking the pages. */
  files(query: ListFilesQuery = {}): AsyncGenerator<File> {
    return this.t.paginate<File>("/v1/files", { query });
  }

  /** One file's metadata. */
  getFile(id: string): Promise<File> {
    return this.t.request("GET", `/v1/files/${encodeURIComponent(id)}`);
  }

  /**
   * A file's bytes and its content type.
   *
   * `range` is an HTTP range such as `bytes=0-1048575`, or omitted for the
   * whole file. Worth using for anything large: the response is proxied and
   * bounded, so a big file is fetched in parts rather than in one call that
   * cannot finish.
   */
  download(id: string, range?: string): Promise<FileContent> {
    return this.t.requestBytes("GET", `/v1/files/${encodeURIComponent(id)}/content`, { range });
  }

  /** Renames a file, moves it, or both. */
  updateFile(id: string, changes: { name?: string; folder_id?: string }): Promise<File> {
    return this.t.request("PATCH", `/v1/files/${encodeURIComponent(id)}`, { body: changes });
  }

  /** Moves a file to the trash. Nothing here frees the bytes. */
  deleteFile(id: string): Promise<void> {
    return this.t.request("DELETE", `/v1/files/${encodeURIComponent(id)}`);
  }

  /**
   * Opens a resumable session and answers the part size to slice by.
   *
   * The way to upload anything large: a single request is capped at the edge
   * and cut at 100 seconds, so past a certain size one call cannot succeed
   * however patient the caller is.
   */
  startUpload(upload: NewUpload): Promise<UploadSession> {
    return this.t.request("POST", "/v1/files/uploads", { body: upload });
  }

  /**
   * Sends one part's bytes.
   *
   * Parts count from 1, and re-sending a number overwrites it: a part whose
   * response was lost is simply sent again rather than restarting the upload.
   */
  uploadPart(sessionId: string, part: number, bytes: Uint8Array): Promise<UploadedPart> {
    return this.t.request(
      "PUT",
      `/v1/files/uploads/${encodeURIComponent(sessionId)}/parts/${part}`,
      { rawBody: bytes, contentType: "application/octet-stream" }
    );
  }

  /**
   * Which parts have landed.
   *
   * The point of a resumable upload: after an interruption, send only what is
   * missing. A read, so it needs files.read where the rest of the session needs
   * files.write.
   */
  uploadStatus(sessionId: string): Promise<UploadSession> {
    return this.t.request("GET", `/v1/files/uploads/${encodeURIComponent(sessionId)}`);
  }

  /** Assembles the parts into a file. */
  completeUpload(sessionId: string): Promise<File> {
    return this.t.request("POST", `/v1/files/uploads/${encodeURIComponent(sessionId)}/complete`);
  }

  /**
   * Abandons a session and discards its parts.
   *
   * Worth calling when you give up: the parts already stored count against the
   * workspace until the session is abandoned.
   */
  abortUpload(sessionId: string): Promise<void> {
    return this.t.request("DELETE", `/v1/files/uploads/${encodeURIComponent(sessionId)}`);
  }

  /**
   * Who a file or document is shared with.
   *
   * Needs manage access on the thing itself, not only the scope: who something
   * is shared with is not something a viewer may read.
   */
  listPermissions(id: string): Promise<Permission[]> {
    return this.t.request("GET", `/v1/files/${encodeURIComponent(id)}/permissions`);
  }

  /**
   * Grants somebody access.
   *
   * A person is named by their workspace user id rather than their email. Needs
   * files.share, which is separate from files.write on purpose: organising a
   * drive and exposing it are different risks.
   */
  share(id: string, permission: Permission): Promise<Permission> {
    return this.t.request("POST", `/v1/files/${encodeURIComponent(id)}/permissions`, {
      body: permission,
    });
  }

  /**
   * Revokes one grant.
   *
   * Takes the permission's id from the listing, not the person's: one person
   * can hold access through more than one grant.
   */
  unshare(id: string, permissionId: string): Promise<void> {
    return this.t.request(
      "DELETE",
      `/v1/files/${encodeURIComponent(id)}/permissions/${encodeURIComponent(permissionId)}`
    );
  }
}
