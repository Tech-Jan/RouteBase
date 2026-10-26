// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	pickAnchor,
	resolveScrollTop,
	parseScrollAnchorState,
	withScrollAnchorState,
	type AnchorCandidate,
	type ScrollAnchorState,
} from './scroll-anchor';

const card = (id: number, top: number, height = 300): AnchorCandidate => ({ id, top, bottom: top + height });

describe('pickAnchor', () => {
	it('picks the measured-topmost card, not the first in the array', () => {
		// The feed is a CSS grid reordered with the `order` property, so DOM order (the array) and
		// visual order (top) disagree the moment the user sorts. Array index must never win.
		const cards = [card(1, 900), card(2, 120), card(3, 500)];

		expect(pickAnchor(cards, 100)).toEqual({ id: 2, dy: 20 });
	});

	it('skips zero-height cards — a display:none card measures all zeros', () => {
		// `.feed-list .activity-card.is-hidden { display: none }`: a filtered-out card's rect is
		// {top:0,bottom:0}. Under "smallest top wins" that phantom would beat every real card.
		const cards: AnchorCandidate[] = [
			{ id: 7, top: 0, bottom: 0 },
			{ id: 8, top: 0, bottom: 0 },
			card(9, 640),
		];

		expect(pickAnchor(cards, 600)).toEqual({ id: 9, dy: 40 });
	});

	it('gives a negative dy for a card straddling the top edge', () => {
		expect(pickAnchor([card(4, 80)], 100)).toEqual({ id: 4, dy: -20 });
	});

	it('ignores cards entirely above the viewport', () => {
		const cards = [card(1, -400, 300), card(2, 250)];

		expect(pickAnchor(cards, 100)).toEqual({ id: 2, dy: 150 });
	});

	it('answers null for an empty list and for an all-scrolled-past list', () => {
		expect(pickAnchor([], 100)).toBeNull();
		expect(pickAnchor([card(1, -900, 300), card(2, -500, 300)], 100)).toBeNull();
	});
});

describe('resolveScrollTop', () => {
	const saved: ScrollAnchorState = { id: 42, dy: 40, top: 8000 };

	it('shifts by how far the anchor drifted from where it was saved', () => {
		// anchor now sits 300px below the edge, was saved at 40 → scroll down the extra 260.
		const top = resolveScrollTop(saved, { currentTop: 8000, maxTop: 20000, viewportTop: 100, anchorTop: 400 });

		expect(top).toBe(8260);
	});

	it('is idempotent — the second pass is a no-op', () => {
		// THE property that makes the post-widget-load pass safe on top of the browser's own scroll
		// anchoring: it recomputes from where the anchor IS, so a corrected page yields no further move.
		const first = resolveScrollTop(saved, { currentTop: 8000, maxTop: 20000, viewportTop: 100, anchorTop: 400 });
		const second = resolveScrollTop(saved, { currentTop: first, maxTop: 20000, viewportTop: 100, anchorTop: 140 });

		expect(second).toBe(first);
	});

	it('falls back to the saved pixel offset when the anchor is gone', () => {
		// Deleted or filtered-out anchor: better to land near where they were than at the top.
		const top = resolveScrollTop(saved, { currentTop: 0, maxTop: 20000, viewportTop: 100, anchorTop: null });

		expect(top).toBe(8000);
	});

	it('clamps into [0, maxTop]', () => {
		const m = { currentTop: 0, maxTop: 500, viewportTop: 100 };

		expect(resolveScrollTop(saved, { ...m, anchorTop: null })).toBe(500);
		expect(resolveScrollTop({ id: 1, dy: 0, top: -50 }, { ...m, anchorTop: null })).toBe(0);
		// A feed that shrank to fit has maxTop 0 (or negative on a fractional layout) — never negative out.
		expect(resolveScrollTop(saved, { ...m, maxTop: -3, anchorTop: null })).toBe(0);
	});
});

describe('parseScrollAnchorState', () => {
	const valid = { id: 42, dy: 40, top: 8000 };

	it('reads a well-formed payload', () => {
		expect(parseScrollAnchorState({ feedScroll: valid }, 'feedScroll')).toEqual(valid);
	});

	it('accepts a null id — "parked above the list, pixels only"', () => {
		const noAnchor = { id: null, dy: 0, top: 120 };

		expect(parseScrollAnchorState({ feedScroll: noAnchor }, 'feedScroll')).toEqual(noAnchor);
	});

	it('answers null for anything else — a fresh history entry and a corrupt one look the same', () => {
		expect(parseScrollAnchorState(null, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState(undefined, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState('feedScroll', 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({}, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: null }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ other: valid }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: { id: '42', dy: 40, top: 8000 } }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: { id: 42, dy: 40 } }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: { id: undefined, dy: 0, top: 0 } }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: { id: 42, dy: Number.NaN, top: 0 } }, 'feedScroll')).toBeNull();
		expect(parseScrollAnchorState({ feedScroll: { id: 42, dy: 0, top: Infinity } }, 'feedScroll')).toBeNull();
	});
});

describe('withScrollAnchorState', () => {
	const pos: ScrollAnchorState = { id: 42, dy: 40, top: 8000 };

	it('keeps keys another module owns', () => {
		const merged = withScrollAnchorState({ replay: { mode: 'on' } }, 'feedScroll', pos);

		expect(merged).toEqual({ replay: { mode: 'on' }, feedScroll: pos });
	});

	it('tolerates a null or non-object prior state', () => {
		expect(withScrollAnchorState(null, 'feedScroll', pos)).toEqual({ feedScroll: pos });
		expect(withScrollAnchorState('junk', 'feedScroll', pos)).toEqual({ feedScroll: pos });
	});

	it('round-trips through parse', () => {
		expect(parseScrollAnchorState(withScrollAnchorState(null, 'segScroll', pos), 'segScroll')).toEqual(pos);
	});
});
