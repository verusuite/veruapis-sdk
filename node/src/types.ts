/**
 * The shapes this API works in.
 *
 * Written by hand, not generated. A generator produces types you reach through
 * rather than types you use: `paths["/v1/messages"]["get"]["responses"][200]
 * ["content"]["application/json"]["data"]` is technically the message list and
 * is not something anybody wants in their editor. These are named, documented
 * and short enough to read.
 *
 * The cost of writing them is drift, so it is paid for: `npm run check-spec`
 * compares every type here against `spec/openapi.yaml` and fails when the API
 * has grown a field these have not. Hand-written and checked beats generated
 * and unusable.
 */

/** Every response is wrapped in this. */
export type Envelope<T> = {
  data: T;
  meta: Meta;
};

export type Meta = {
  /** Quote this when reporting a problem. */
  request_id: string;
  timestamp: string;

  /** Present on a list that has another page. Pass it back as `cursor`. */
  next_cursor?: string;
};

/** A person or address on a message. */
export type Address = {
  email: string;
  name?: string;
};

// ---- mail ----

export type Folder = {
  id: string;
  name: string;
  message_count: number;
  unread_count: number;
};

/** The flags a message carries. */
export type MessageFlags = {
  read: boolean;
  starred: boolean;
  answered: boolean;
  draft: boolean;
  deleted: boolean;
};

/**
 * A message as a listing returns it.
 *
 * No recipients and no body: fetch the message for those. The summary is
 * deliberately small, because a folder listing that carried every body would
 * pull a mailbox through one response.
 */
export type MessageSummary = {
  id: string;
  folder_id: string;
  thread_id: string;
  message_id: string;
  subject: string;
  from: string;
  date: string;
  size: number;
  flags: MessageFlags;
  labels: string[];
};

/** A message, fetched. MIME traversal and header decoding are already done. */
export type Message = {
  id: string;
  message_id: string;
  subject: string;
  date: string;
  from: Address;
  to: Address[];
  cc?: Address[];
  text?: string;
  html?: string;
  headers: Record<string, string>;
  attachments: Attachment[];
};

export type Attachment = {
  part_id: string;
  filename: string;
  content_type: string;
  size: number;
  /** True for an image the HTML body displays rather than a file to download. */
  inline: boolean;
};

/** What sending takes. */
export type SendMessage = {
  to: string[];
  cc?: string[];
  bcc?: string[];
  subject: string;
  text?: string;
  html?: string;
  from?: string;
  reply_to?: string;
  attachments?: OutgoingAttachment[];
};

export type OutgoingAttachment = {
  filename: string;
  content_type: string;
  /** The file, base64 encoded. Padded or not, standard or URL-safe. */
  content_base64: string;
  inline?: boolean;
  /** Required when inline is true. The HTML refers to it as src="cid:the-id". */
  content_id?: string;
};

// ---- calendar ----

export type Calendar = {
  id: string;
  name: string;
  description?: string;
  color?: string;
  /** False for a calendar shared with the user by somebody else. */
  is_owner?: boolean;
};

export type Event = {
  id: string;
  calendar_id: string;
  summary: string;
  description?: string;
  location?: string;
  /** UTC. The offset a calendar app shows is that app's timezone. */
  starts_at: string;
  ends_at: string;
  all_day?: boolean;
  status?: string;
  attendees?: Attendee[];
  recurrence?: string[];
};

export type Attendee = {
  email: string;
  name?: string;
  role?: "REQ-PARTICIPANT" | "OPT-PARTICIPANT" | "NON-PARTICIPANT" | "CHAIR";
  status?: "NEEDS-ACTION" | "ACCEPTED" | "DECLINED" | "TENTATIVE" | "DELEGATED";
};

/** A window somebody is busy in. Free/busy returns these rather than events. */
export type BusyPeriod = {
  start: string;
  end: string;
};

export type FreeBusy = {
  email: string;
  busy: BusyPeriod[];
};

// ---- query shapes ----

/** Cursor paging. Never an offset: rows arriving mid-walk shift one. */
export type PageQuery = {
  limit?: number;
  cursor?: string;
};

export type ListMessagesQuery = PageQuery & {
  folder_id?: string | string[];
  unread?: boolean;
  flag?: "read" | "starred" | "answered" | "draft" | "deleted";
  label?: string | string[];
};

export type ListEventsQuery = PageQuery & {
  /** Required. A recurring series expands without limit, so a window is not optional. */
  start: string;
  end: string;
  calendar_id?: string | string[];
};

/** A spreadsheet's structure: its tabs and what is on them. */
export type Workbook = {
  document_id: string;
  sheets: Sheet[];
};

/** One tab, with the extent of what is actually populated. */
export type Sheet = {
  id: string;
  name: string;
  index: number;
  /** Last populated row and column, zero-based. An unbounded range like A:A is clamped to these. */
  max_row: number;
  max_column: number;
  range: string;
  merges: Merge[];
};

/** One merged rectangle, zero-based and inclusive at both corners. */
export type Merge = { id: string; c0: number; r0: number; c1: number; r1: number };

/** A document's change token: poll it, or send it as the precondition on a structural change. */
export type DocumentState = {
  document_id: string;
  state: string;
  checked_at: string;
};

/** A cell is whatever fits: a string, a number, a boolean, or a formula written as a string beginning with "=". */
export type Cell = string | number | boolean | null;

/**
 * One rectangle of cells.
 *
 * Reads are always rectangular — an empty cell arrives as an empty string — so
 * you need not bounds-check each row.
 */
export type ValueRange = {
  range: string;
  values: Cell[][];
};

/** What a value write reports. */
export type WriteResult = {
  /** Where the values landed. Present on a single-range write and an append. */
  range?: string;
  updated_cells?: number;
  /** The document's state after the write. Keep it to notice somebody else's edit. */
  state: string;
  /**
   * False when the write stored nothing new — writing a cell the value it
   * already holds. Not an error, and not worth retrying: nothing was stored and
   * nobody with the document open was told.
   */
  changed: boolean;
};

/** What a structural batch reports. Replies are positional, one per request. */
export type StructureResult = {
  replies: Record<string, unknown>[];
  state: string;
  changed: boolean;
};

/**
 * Stored returns what is in the cell, so a formula comes back as its own text.
 * Computed evaluates it, which costs a pass over the whole workbook and is
 * unavailable when no formula engine is running.
 */
export type ValueRender = "stored" | "computed";

/** One structural change. Exactly one operation per entry. */
export type SheetRequest = Record<string, unknown>;

/** The user a key acts as. A key can never do more than they can. */
export type Profile = {
  id: string;
  email: string;
  name: string;
  role: string;
  workspace: { id: string };
  /** Empty for an account with no mailbox. */
  mailbox_address?: string;
  avatar_url?: string;
};

/** One workspace group as a member sees it. */
export type Group = {
  id: string;
  name: string;
  description?: string;
  /**
   * null when withheld, which happens for a group the caller cannot see into.
   * null is not zero: it means unknown, not empty.
   */
  member_count: number | null;
  updated_at?: string;
};

/** One address book in the caller's mailbox. */
export type AddressBook = {
  id: string;
  name: string;
  description?: string;
  contact_count: number;
  created_at?: string;
  updated_at?: string;
};

export type ContactEmail = { address: string; type?: string };
export type ContactPhone = { number: string; type?: string };

/** One entry in an address book. */
export type Contact = {
  id?: string;
  address_book_id?: string;
  name?: string;
  given_name?: string;
  family_name?: string;
  emails?: ContactEmail[];
  phones?: ContactPhone[];
  organization?: string;
  title?: string;
  notes?: string;
  created_at?: string;
  updated_at?: string;
};

export type ListContactsQuery = {
  address_book_id?: string;
  /** Matches the name and every address, including secondary ones. */
  q?: string;
  limit?: number;
  cursor?: string;
};

/** A document or a spreadsheet. An uploaded file is a File, not one of these. */
export type Document = {
  id?: string;
  type?: "document" | "spreadsheet";
  title?: string;
  /** "active" or "trashed". Deleting trashes; nothing here destroys. */
  state?: string;
  /** Who owns it, which is not necessarily the key's owner. */
  owner_id?: string;
  /** Empty for a document in the root. */
  folder_id?: string;
  /** Yours to shape. The API stores it and does not read it. */
  metadata?: Record<string, unknown>;
  created_at?: string;
  updated_at?: string;
};

export type ListDocumentsQuery = {
  type?: "document" | "spreadsheet";
  folder_id?: string;
  q?: string;
  trashed?: boolean;
  limit?: number;
  cursor?: string;
};

/** One comment or reply on a document. */
export type Comment = {
  id?: string;
  document_id?: string;
  /** Set on a reply, absent on a thread's first comment. */
  parent_id?: string;
  author_id?: string;
  body?: string;
  /** "open" or "resolved". */
  state?: string;
  created_at?: string;
  updated_at?: string;
};

/** One grant of access to a document or file. */
export type Permission = {
  id?: string;
  /** "user" or "group"; principal_id is that id, not an email. */
  principal_type?: string;
  principal_id?: string;
  role?: string;
  created_by?: string;
  created_at?: string;
};

/** An uploaded file. */
export type File = {
  id?: string;
  type?: string;
  /** The display name, which can change. */
  title?: string;
  /** What it was uploaded as, which does not. */
  filename?: string;
  /** Sniffed from the bytes when stored, not taken from what was declared. */
  mime_type?: string;
  size_bytes?: number;
  state?: string;
  owner_id?: string;
  folder_id?: string;
  created_at?: string;
  updated_at?: string;
};

export type ListFilesQuery = {
  folder_id?: string;
  q?: string;
  trashed?: boolean;
  limit?: number;
  cursor?: string;
};

/** One folder in the drive. */
export type FileFolder = {
  id?: string;
  name?: string;
  /** Empty for a folder at the root. */
  parent_id?: string;
  owner_id?: string;
  trashed?: boolean;
  created_at?: string;
  updated_at?: string;
};

/** An upload in progress. */
export type UploadSession = {
  session_id: string;
  /** The file this will become, reserved before the bytes are all up. */
  document_id: string;
  /** Slice by exactly this, every part but the last. */
  part_size: number;
  total_size: number;
  status?: string;
  /** Which numbers have landed. Read from storage, so a resume survives a restart. */
  uploaded_parts?: number[];
  uploaded_bytes?: number;
};

export type UploadedPart = { part: number; etag: string };

/** A file about to be sent. */
export type NewUpload = {
  filename: string;
  /** Required: it decides the part size that comes back. */
  size: number;
  /** Recorded, not trusted: the stored type is sniffed from the bytes. */
  mime_type?: string;
  folder_id?: string;
};

/** A downloaded file: the bytes, and what they are. */
export type FileContent = { bytes: Uint8Array; contentType: string };
