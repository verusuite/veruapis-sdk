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
