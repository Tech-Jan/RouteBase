/**
 * scroll-anchor.ts — the geometry behind "put me back where I was", as pure functions.
 *
 * Split from core/scroll-restore.ts for one reason: jsdom has no layout engine, so every
 * getBoundingClientRect() there returns zeros. Geometry that lives in the DOM wrapper is geometry
 * that can only be tested by hand. Everything here takes measured NUMBERS and is tested under the
 * node environment.
 *
 * Why an anchor rather than a raw scrollTop: the offset of a list is not stable across a reload.
 * The dashboard's right rail stacks ABOVE the feed below 1600px and its widgets are async, so the
 * feed moves after first paint. Remembering "card 812 was 40px below the top edge" survives that;
 * remembering "scrollTop was 8000" does not.
 */

/** One measured item in the scroller — `top`/`bottom` are viewport coordinates (rect coords). */
export interface AnchorCandidate {
	readonly id: number;
	readonly top: number;
	readonly bottom: number;
}

/** What gets persisted into the history entry. `top` is the pixel fallback for a vanished anchor. */
export interface ScrollAnchorState {
	/** Item the viewport's top edge was resting on. Null when nothing was — e.g. parked above the list. */
	readonly id: number | null;
	/** How far BELOW the viewport's top edge that item started. Negative when it straddles the edge. */
	readonly dy: number;
	/** The raw scrollTop at capture time — used when the anchor item is gone (deleted, filtered out). */
	readonly top: number;
}

/** Everything resolveScrollTop needs, measured at restore time. */
export interface AnchorMeasurement {
	/** scroller.scrollTop right now. */
	readonly currentTop: number;
	/** scroller.scrollHeight - scroller.clientHeight. */
	readonly maxTop: number;
	/** The scroller's own rect top — the viewport edge everything is relative to. */
	readonly viewportTop: number;
	/** The saved anchor's rect top now, or null if it no longer exists or is hidden. */
	readonly anchorTop: number | null;
}

/**
 * The topmost item still intersecting the viewport, with its offset from the top edge.
 *
 * "Topmost" is decided by MEASURED position, never by array order: the feed is a CSS grid whose
 * cards are reordered with the `order` property, so DOM order and visual order disagree the moment
 * the user sorts.
 *
 * Zero-height candidates are skipped. A filtered-out card is `display:none`, whose rect is all
 * zeros — under a "smallest top wins" rule that phantom at 0 would beat every real card.
 */
export function pickAnchor(
	cards: readonly AnchorCandidate[],
	viewportTop: number,
): { id: number; dy: number } | null {
	let best: AnchorCandidate | null = null;
	for (const card of cards) {
		if (card.bottom <= card.top) continue;      // display:none — see above
		if (card.bottom <= viewportTop) continue;   // fully scrolled past
		if (best === null || card.top < best.top) best = card;
	}
	return best === null ? null : { id: best.id, dy: best.top - viewportTop };
}

/**
 * The scrollTop that puts the saved anchor back under the viewport's top edge.
 *
 * Recomputed from scratch every call — it reads where the anchor IS and asks for where it SHOULD be,
 * so calling it twice is a no-op the second time. That idempotence is what makes the second restore
 * pass safe: Chrome and Firefox implement scroll anchoring (nothing in this app sets
 * `overflow-anchor: none`), so they may already have compensated for content growing above us. An
 * incremental "shift by the delta we saw" pass would double-correct on those browsers.
 */
export function resolveScrollTop(saved: ScrollAnchorState, m: AnchorMeasurement): number {
	const target = m.anchorTop === null
		? saved.top                                              // anchor gone — fall back to pixels
		: m.currentTop + (m.anchorTop - m.viewportTop) - saved.dy;
	return clamp(target, 0, Math.max(0, m.maxTop));
}

/**
 * Read our payload out of an untrusted `history.state`.
 *
 * `history.state` is whatever any other module put there (and survives a reload), so every field is
 * checked. Anything unrecognised answers null, which the caller reads as "fresh entry, do not
 * restore" — the same answer a brand-new history entry gives.
 */
export function parseScrollAnchorState(state: unknown, stateKey: string): ScrollAnchorState | null {
	if (typeof state !== 'object' || state === null) return null;
	const raw: unknown = (state as Record<string, unknown>)[stateKey];
	if (typeof raw !== 'object' || raw === null) return null;
	const { id, dy, top } = raw as Record<string, unknown>;
	if (id !== null && !isFiniteNumber(id)) return null;
	if (!isFiniteNumber(dy) || !isFiniteNumber(top)) return null;
	return { id, dy, top };
}

/** Merge our payload into `history.state` without dropping keys another module owns. */
export function withScrollAnchorState(
	state: unknown,
	stateKey: string,
	pos: ScrollAnchorState,
): Record<string, unknown> {
	const base = typeof state === 'object' && state !== null ? (state as Record<string, unknown>) : {};
	return { ...base, [stateKey]: pos };
}

function isFiniteNumber(v: unknown): v is number {
	return typeof v === 'number' && Number.isFinite(v);
}

function clamp(v: number, lo: number, hi: number): number {
	return v < lo ? lo : v > hi ? hi : v;
}
