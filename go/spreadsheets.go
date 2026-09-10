package veruapis

import (
	"context"
	"net/http"
	"net/url"
)

// SpreadsheetsService is the contents of a spreadsheet: reading and writing
// ranges, appending rows, and the structural changes that move data around.
//
// A spreadsheet's lifecycle — creating, renaming, trashing — is Documents,
// because a spreadsheet is a document. These are its cells.
//
// The split between the two write scopes is worth knowing before you grant a
// key: writing values needs sheets.values.write and merges cleanly with
// whatever else is happening in the document, while inserting or deleting rows
// needs sheets.structure.write and moves everything below it. A script that
// fills in a weekly figure should hold only the first.
type SpreadsheetsService struct{ client *Client }

// Workbook is a spreadsheet's structure.
type Workbook struct {
	DocumentID string  `json:"document_id"`
	Sheets     []Sheet `json:"sheets"`
}

// Sheet is one tab, with the extent of what is actually on it.
type Sheet struct {
	ID    string `json:"id"`
	Name  string `json:"name"`
	Index int    `json:"index"`

	// MaxRow and MaxColumn are the last populated row and column, zero-based.
	// An unbounded range like A:A is clamped to this, not to the million rows
	// a grid permits.
	MaxRow    int     `json:"max_row"`
	MaxColumn int     `json:"max_column"`
	Range     string  `json:"range"`
	Merges    []Merge `json:"merges"`
}

// Merge is one merged rectangle, zero-based and inclusive at both corners.
type Merge struct {
	ID string `json:"id"`
	C0 int    `json:"c0"`
	R0 int    `json:"r0"`
	C1 int    `json:"c1"`
	R1 int    `json:"r1"`
}

// DocumentState is a document's change token.
type DocumentState struct {
	DocumentID string `json:"document_id"`
	State      string `json:"state"`
	CheckedAt  string `json:"checked_at"`
}

// ValueRange is one rectangle of cells.
//
// Values is rows of cells, and a cell is whatever fits: a string, a number, a
// bool, or a formula written as a string beginning with "=". Reads are always
// rectangular — an empty cell arrives as an empty string — so a caller need not
// bounds-check each row.
type ValueRange struct {
	Range  string  `json:"range"`
	Values [][]any `json:"values"`
}

// WriteResult is what a value write reports.
type WriteResult struct {
	// Range is where the values landed. Set on a single-range write and on an
	// append, empty on a batch that spans several.
	Range string `json:"range,omitempty"`

	UpdatedCells int `json:"updated_cells,omitempty"`

	// State is the document's state after the write. Keep it to notice
	// somebody else's edit, or to send as the precondition on a structural
	// change.
	State string `json:"state"`

	// Changed is false when the write stored nothing new — writing a cell the
	// value it already holds. Not an error and not worth retrying: nothing was
	// stored and nobody with the document open was told.
	Changed bool `json:"changed"`
}

// StructureResult is what a structural batch reports.
type StructureResult struct {
	// Replies is positional: one entry per request, in the order sent, empty
	// where an operation returns nothing.
	Replies []map[string]any `json:"replies"`
	State   string           `json:"state"`
	Changed bool             `json:"changed"`
}

// ValueRender selects stored inputs or evaluated results.
const (
	// RenderStored returns what is in the cell, so a formula comes back as its
	// own text. The default, and it always works.
	RenderStored = "stored"

	// RenderComputed evaluates formulas and returns the results. It costs a
	// pass over the whole workbook rather than over the range, because a
	// formula in your range can depend on a cell far outside it, and it is
	// unavailable when no formula engine is running.
	RenderComputed = "computed"
)

// Get returns the workbook: every sheet with its extent and merges.
//
// This is how you list the tabs; there is no separate call for them. Read it
// first to learn the sheet names an A1 range needs.
func (s *SpreadsheetsService) Get(ctx context.Context, spreadsheetID string) (Workbook, error) {
	out, _, err := Do[Workbook](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/spreadsheets/" + url.PathEscape(spreadsheetID),
	})
	return out, err
}

// State returns the document's change token.
//
// Two uses, one value: poll it to notice somebody else's change — there are no
// webhooks for spreadsheets — and pass it to ApplyStructure, which refuses to
// work without it.
func (s *SpreadsheetsService) State(ctx context.Context, spreadsheetID string) (DocumentState, error) {
	out, _, err := Do[DocumentState](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   "/v1/spreadsheets/" + url.PathEscape(spreadsheetID) + "/state",
	})
	return out, err
}

// Values reads one A1 range.
//
// render is RenderStored or RenderComputed; empty means stored.
func (s *SpreadsheetsService) Values(ctx context.Context, spreadsheetID, rng, render string) (ValueRange, error) {
	out, _, err := Do[ValueRange](ctx, s.client, Request{
		Method: http.MethodGet,
		Path:   valuesPath(spreadsheetID, rng),
		Query:  renderQuery(render),
	})
	return out, err
}

// BatchValues reads several ranges in one call.
//
// One request rather than one per range, and the cell cap applies to the whole
// call. With RenderComputed the workbook is evaluated once for all of them.
func (s *SpreadsheetsService) BatchValues(ctx context.Context, spreadsheetID string, ranges []string, render string) ([]ValueRange, error) {
	type batch struct {
		ValueRanges []ValueRange `json:"value_ranges"`
	}
	out, _, err := Do[batch](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/spreadsheets/" + url.PathEscape(spreadsheetID) + "/values/batch-get",
		Query:  renderQuery(render),
		Body:   map[string]any{"ranges": ranges},
	})
	return out.ValueRanges, err
}

// Write overwrites one range.
//
// Values are anchored at the range's top-left corner, and a block shorter than
// the range leaves the rest untouched: writing two rows into a ten-row range
// writes two rows. Clear is a separate call for that reason.
//
// An empty string deletes a cell rather than storing a blank, which would keep
// it inside the sheet's used extent and widen every unbounded range from then
// on.
func (s *SpreadsheetsService) Write(ctx context.Context, spreadsheetID, rng string, values [][]any) (WriteResult, error) {
	out, _, err := Do[WriteResult](ctx, s.client, Request{
		Method: http.MethodPut,
		Path:   valuesPath(spreadsheetID, rng),
		Body:   map[string]any{"values": values},
	})
	return out, err
}

// BatchWrite writes several ranges as one document write, so somebody with the
// sheet open sees one change rather than a flicker of several.
func (s *SpreadsheetsService) BatchWrite(ctx context.Context, spreadsheetID string, data []ValueRange) (WriteResult, error) {
	out, _, err := Do[WriteResult](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   "/v1/spreadsheets/" + url.PathEscape(spreadsheetID) + "/values/batch-update",
		Body:   map[string]any{"data": data},
	})
	return out, err
}

// Append adds rows after the last populated row of the range's own columns.
//
// Of the sheet's own columns, not the sheet: an unrelated note in column Z does
// not push your table down. This is the call a log or a nightly export wants —
// it needs no read first, and two appends cannot overwrite each other.
func (s *SpreadsheetsService) Append(ctx context.Context, spreadsheetID, rng string, values [][]any) (WriteResult, error) {
	out, _, err := Do[WriteResult](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   valuesPath(spreadsheetID, rng) + "/append",
		Body:   map[string]any{"values": values},
	})
	return out, err
}

// Clear empties a range and leaves its formatting, so a template keeps its
// headers, colours and number formats.
func (s *SpreadsheetsService) Clear(ctx context.Context, spreadsheetID, rng string) (WriteResult, error) {
	out, _, err := Do[WriteResult](ctx, s.client, Request{
		Method: http.MethodPost,
		Path:   valuesPath(spreadsheetID, rng) + "/clear",
	})
	return out, err
}

// ApplyStructure inserts and deletes rows, columns and sheets, and changes
// merges, sorting and formatting.
//
// state comes from State and is required: these are the changes that move data
// other requests address by position, so one applied to a document that has
// moved on merges cleanly into a corrupt grid. A stale token is refused with a
// conflict rather than applied — read the state again and reapply.
//
// Requests apply in order and each sees the effect of the one before, so adding
// a sheet and formatting it in one batch works:
//
//	st, _ := api.Spreadsheets.State(ctx, id)
//	api.Spreadsheets.ApplyStructure(ctx, id, st.State, []map[string]any{
//		{"add_sheet": map[string]any{"title": "Q4"}},
//		{"repeat_cell": map[string]any{
//			"range": "Q4!A1:D1",
//			"style": map[string]any{"b": 1},
//		}},
//	})
func (s *SpreadsheetsService) ApplyStructure(ctx context.Context, spreadsheetID, state string, requests []map[string]any) (StructureResult, error) {
	out, _, err := Do[StructureResult](ctx, s.client, Request{
		Method:  http.MethodPost,
		Path:    "/v1/spreadsheets/" + url.PathEscape(spreadsheetID) + "/batch-update",
		IfMatch: state,
		Body:    map[string]any{"requests": requests},
	})
	return out, err
}

// valuesPath builds a range path.
//
// The range is escaped as one segment: A1 notation carries characters a URL
// reads as structure — a quoted sheet name, a space, a colon — and a range is
// never a path.
func valuesPath(spreadsheetID, rng string) string {
	return "/v1/spreadsheets/" + url.PathEscape(spreadsheetID) + "/values/" + url.PathEscape(rng)
}

func renderQuery(render string) url.Values {
	if render == "" || render == RenderStored {
		return nil
	}
	return url.Values{"value_render": []string{render}}
}
