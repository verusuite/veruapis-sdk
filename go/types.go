package veruapis

// The shapes this API works in.
//
// Written by hand, not generated. A generator produces types you reach through
// rather than types you use, and these are short enough to read. The cost of
// writing them is drift, so it is paid for: spec_test.go compares every route
// this client calls against the API's own description and fails when the two
// disagree.

// Address is a person on a message.
type Address struct {
	Email string `json:"email"`
	Name  string `json:"name,omitempty"`
}

// ---- mail ----

// Folder is one mail folder with its counts.
type Folder struct {
	ID           string `json:"id"`
	Name         string `json:"name"`
	MessageCount int    `json:"message_count"`
	UnreadCount  int    `json:"unread_count"`
}

// Flags are the states a message carries.
type Flags struct {
	Read     bool `json:"read"`
	Starred  bool `json:"starred"`
	Answered bool `json:"answered"`
	Draft    bool `json:"draft"`
	Deleted  bool `json:"deleted"`
}

// MessageSummary is a message as a listing returns it.
//
// No recipients and no body: fetch the message for those. The summary is small
// on purpose, because a folder listing carrying every body would pull a whole
// mailbox through one response.
type MessageSummary struct {
	ID        string   `json:"id"`
	FolderID  string   `json:"folder_id"`
	ThreadID  string   `json:"thread_id"`
	MessageID string   `json:"message_id"`
	Subject   string   `json:"subject"`
	From      string   `json:"from"`
	Date      string   `json:"date"`
	Size      int      `json:"size"`
	Flags     Flags    `json:"flags"`
	Labels    []string `json:"labels"`
}

// Message is one message, fetched. MIME traversal, transfer decoding and header
// decoding are already done.
type Message struct {
	ID        string            `json:"id"`
	MessageID string            `json:"message_id"`
	Subject   string            `json:"subject"`
	Date      string            `json:"date"`
	From      Address           `json:"from"`
	To        []Address         `json:"to"`
	CC        []Address         `json:"cc,omitempty"`
	Text      string            `json:"text,omitempty"`
	HTML      string            `json:"html,omitempty"`
	Headers   map[string]string `json:"headers,omitempty"`

	Attachments []Attachment `json:"attachments"`
}

// Attachment is one part of a message. Bytes are a separate call, so reading a
// message never drags a large file through the response.
type Attachment struct {
	PartID      string `json:"part_id"`
	Filename    string `json:"filename"`
	ContentType string `json:"content_type"`
	Size        int    `json:"size"`

	// Inline is true for an image the HTML body displays rather than a file to
	// download.
	Inline bool `json:"inline"`
}

// SendMessage is what sending takes.
type SendMessage struct {
	To      []string `json:"to"`
	CC      []string `json:"cc,omitempty"`
	BCC     []string `json:"bcc,omitempty"`
	Subject string   `json:"subject"`
	Text    string   `json:"text,omitempty"`
	HTML    string   `json:"html,omitempty"`
	From    string   `json:"from,omitempty"`
	ReplyTo string   `json:"reply_to,omitempty"`

	Attachments []OutgoingAttachment `json:"attachments,omitempty"`
}

// OutgoingAttachment is a file to send.
type OutgoingAttachment struct {
	Filename    string `json:"filename"`
	ContentType string `json:"content_type"`

	// ContentBase64 is the file, base64 encoded. Padded or not, standard or
	// URL-safe.
	ContentBase64 string `json:"content_base64"`

	Inline bool `json:"inline,omitempty"`

	// ContentID is required when Inline is true. The HTML refers to it as
	// src="cid:the-id".
	ContentID string `json:"content_id,omitempty"`
}

// Sent is what the API answers when a message is accepted.
//
// A 202, not a 200: the message is queued and archived in Sent, and delivery
// happens afterwards. That is not a promise it arrived, and this API cannot
// tell you that it did. A failure comes back later as a bounce.
type Sent struct {
	ID string `json:"id"`
}

// ---- calendar ----

// Calendar is one calendar.
type Calendar struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Description string `json:"description,omitempty"`
	Color       string `json:"color,omitempty"`

	// IsOwner is false for a calendar somebody shared with this user.
	IsOwner bool `json:"is_owner,omitempty"`
}

// Event is one occurrence.
//
// Times are UTC. The offset a calendar application shows is that application's
// timezone, so anything scheduling by wall-clock time needs the user's zone
// from elsewhere.
type Event struct {
	ID          string `json:"id,omitempty"`
	CalendarID  string `json:"calendar_id,omitempty"`
	Summary     string `json:"summary"`
	Description string `json:"description,omitempty"`
	Location    string `json:"location,omitempty"`

	StartsAt string `json:"starts_at"`
	EndsAt   string `json:"ends_at"`
	AllDay   bool   `json:"all_day,omitempty"`
	Status   string `json:"status,omitempty"`

	Attendees  []Attendee `json:"attendees,omitempty"`
	Recurrence []string   `json:"recurrence,omitempty"`
}

// Attendee is somebody invited.
type Attendee struct {
	Email string `json:"email"`
	Name  string `json:"name,omitempty"`

	// Role is REQ-PARTICIPANT, OPT-PARTICIPANT, NON-PARTICIPANT or CHAIR.
	Role string `json:"role,omitempty"`

	// Status is NEEDS-ACTION, ACCEPTED, DECLINED, TENTATIVE or DELEGATED.
	Status string `json:"status,omitempty"`
}

// BusyPeriod is a window somebody is busy in.
type BusyPeriod struct {
	Start string `json:"start"`
	End   string `json:"end"`
}

// FreeBusy is one person's busy windows.
//
// Free/busy answers availability without returning event detail, so it needs
// far less access than reading everybody's calendar to work the same thing out.
type FreeBusy struct {
	Email string       `json:"email"`
	Busy  []BusyPeriod `json:"busy"`
}

// ---- queries ----

// ListMessagesOptions narrows a message listing.
//
// At least one filter is required. An unfiltered list would be the whole
// mailbox, so the API refuses it rather than quietly returning the inbox.
type ListMessagesOptions struct {
	FolderID []string
	Unread   bool
	Flag     string
	Label    []string

	Limit int

	// Cursor comes from the previous page's Meta.NextCursor. Prefer the
	// iterators, which carry it for you.
	Cursor string
}

// ListEventsOptions narrows an event listing.
type ListEventsOptions struct {
	// Start and End are required. A recurring series expands without limit, so
	// there is no finite answer to "all events".
	Start string
	End   string

	CalendarID []string
	Limit      int
	Cursor     string
}
