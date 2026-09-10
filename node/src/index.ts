export { VeruApi, VeruApiError } from "./client.js";
export type { ClientOptions, RequestOptions, Method, Transport } from "./client.js";
export { Mail, CalendarApi, Spreadsheets, Documents, Files, Identity, Contacts } from "./resources.js";

// The shapes the API works in. Written by hand and checked against the API's
// own description by "npm run check-spec".
export type * from "./types.js";
