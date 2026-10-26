// dom-events.ts — names of cross-entry document CustomEvents. Dispatcher and listener ship as
// separate ESM entries, so they talk over the DOM; the event NAME lives here so the two sides
// can't drift (same pattern as dashboard-filters' SCOPE_EVENT). Tests keep the raw literal on
// purpose — they pin the wire value.

/** Friends page resolved a pending request — the nav badge refreshes immediately (SO-03). */
export const FRIEND_REQUEST_RESOLVED_EVENT = 'friend-request-resolved';

/** The background-operation progress toast finished its operation and hid itself. */
export const OP_TOAST_HIDDEN_EVENT = 'opToastHidden';
