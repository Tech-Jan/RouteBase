/**
 * scroll-restore.ts — return a long list to where the user left it on browser Back.
 *
 * None of this app's list pages scroll the DOCUMENT: `.cockpit-shell` is `height:100dvh;
 * overflow:hidden` and the scroller is a `.cockpit-scroll` div. The browser's own scroll restoration
 * only ever restores document scroll, so it does nothing here — hence this module.
 *
 * THE POSITION LIVES IN `history.state`, not in web storage, and that choice does the gating for us:
 *
 *   - It is scoped to ONE history entry in ONE tab. Two tabs at different offsets cannot overwrite
 *     each other, which one shared device-preference key could not avoid.
 *   - A freshly created entry has `state === null`, so "payload present ⇒ restore" already means
 *     Back/Forward and reload restore while a fresh visit does not. No navigation-type check, no TTL,
 *     nothing to keep in sync with that rule.
 *   - It is also not web storage, so it stays clear of core/device-prefs.ts's ownership of that seam.
 *
 * The cost is that every `history.replaceState` on the page must preserve state rather than pass
 * null — see the grid `onQueryChange` handlers, which fire a microtask after their grid is built.
 * That is also why the payload is read in the constructor, synchronously, before anything else runs.
 *
 * The geometry itself is in core/scroll-anchor.ts, pure and unit-tested — jsdom has no layout engine.
 */

import { afterLayout } from './after-layout';
import { readDataNumberOrNull, dataAttrName } from './data-attrs';
import {
	pickAnchor,
	resolveScrollTop,
	parseScrollAnchorState,
	withScrollAnchorState,
	type AnchorCandidate,
	type ScrollAnchorState,
} from './scroll-anchor';

export interface ScrollRestore {
	/** Measure now and commit into the history entry. Wired to pagehide + visibilitychange. */
	capture(): void;
	/** Re-apply the saved position after layout settles. Idempotent; a no-op once the user has moved. */
	restore(): void;
	destroy(): void;
}

/** How to identify anchorable items. Both halves or neither — a selector without an id is unusable. */
export interface ScrollAnchorSource {
	/** Anchorable items inside the scroller, e.g. `.activity-card[data-track-id]`. */
	itemSelector: string;
	/** data-* key holding each item's numeric id, in camelCase (`trackId` → `data-track-id`). */
	idAttr: string;
}

export interface ScrollRestoreOptions {
	/** The element that actually scrolls — `.cockpit-scroll`, not the window. */
	scroller: HTMLElement;
	/** Grep-able key this page owns inside `history.state`. */
	stateKey: string;
	/**
	 * Omit for a pixel-only restore. That is right when the scroller's own geometry cannot shift
	 * between capture and restore — a constant-size window over fixed-height rows rendered
	 * synchronously (`.act-table-wrapper`) has nothing for an anchor to protect against.
	 */
	anchor?: ScrollAnchorSource;
}

export function createScrollRestore(opts: ScrollRestoreOptions): ScrollRestore {
	const { scroller, anchor, stateKey } = opts;

	// Synchronously, before anything else on the page can touch history.state — a grid's
	// onQueryChange fires one microtask after its constructor and used to overwrite the whole state.
	const saved = parseScrollAnchorState(history.state, stateKey);

	// Tracks user INTENT to scroll, not the `scroll` event: our own `scrollTop =` fires `scroll` too,
	// and telling the two apart is a race. None of these four can be provoked by a programmatic scroll.
	let userMoved = false;
	const markMoved = (): void => { userMoved = true; };

	const onVisibility = (): void => {
		if (document.visibilityState === 'hidden') capture();
	};

	/**
	 * "Which item is at the top edge?" answered by hit-test rather than by measuring every item.
	 *
	 * The scan below is O(n) rects to answer an O(1) question, and capture() runs on every
	 * visibilitychange — i.e. every tab switch and every app-background on mobile, not just on
	 * navigation. With several hundred cards that is real work for nothing.
	 *
	 * Probed at three x offsets because the feed is a TWO-COLUMN grid: a single probe down the middle
	 * lands in the column gap. Any column's card is an equally good anchor (grid rows share a top), so
	 * the first hit wins. Returns null when the point lands on padding or an overlay, and the caller
	 * falls back to the scan.
	 */
	function anchorByHitTest(viewportTop: number): { id: number; dy: number } | null {
		// Optional fast path, so it feature-detects rather than assumes: capture() runs on pagehide, and
		// a TypeError there would lose the position silently mid-navigation. jsdom has no hit-testing at
		// all, which is exactly the environment that would otherwise blow up.
		if (!anchor || typeof document.elementFromPoint !== 'function') return null;
		const box = scroller.getBoundingClientRect();
		for (const frac of [0.25, 0.5, 0.75]) {
			const hit = document.elementFromPoint(box.left + box.width * frac, viewportTop + 1);
			const el = hit?.closest<HTMLElement>(anchor.itemSelector);
			if (!el || !scroller.contains(el)) continue;
			const id = readDataNumberOrNull(el, anchor.idAttr);
			if (id === null) continue;
			return { id, dy: el.getBoundingClientRect().top - viewportTop };
		}
		return null;
	}

	/** The exhaustive fallback: measure everything and let pickAnchor decide. */
	function candidates(): AnchorCandidate[] {
		const out: AnchorCandidate[] = [];
		if (!anchor) return out;
		for (const el of scroller.querySelectorAll<HTMLElement>(anchor.itemSelector)) {
			const id = readDataNumberOrNull(el, anchor.idAttr);
			if (id === null) continue;
			const r = el.getBoundingClientRect();
			out.push({ id, top: r.top, bottom: r.bottom });
		}
		return out;
	}

	/** Where the saved anchor sits now — null when it is gone, or hidden by a filter (zero rect). */
	function anchorTop(id: number | null): number | null {
		if (id === null || !anchor) return null;
		// One selector-engine lookup. This used to walk every item comparing data attributes.
		const el = scroller.querySelector<HTMLElement>(
			`${anchor.itemSelector}[${dataAttrName(anchor.idAttr)}="${id}"]`,
		);
		if (!el) return null;
		const r = el.getBoundingClientRect();
		return r.bottom > r.top ? r.top : null;
	}

	function capture(): void {
		const viewportTop = scroller.getBoundingClientRect().top;
		const found = anchorByHitTest(viewportTop) ?? pickAnchor(candidates(), viewportTop);
		const pos: ScrollAnchorState = {
			id: found?.id ?? null,
			dy: found?.dy ?? 0,
			top: scroller.scrollTop,
		};
		// No URL argument: the grids own the query string, and passing one here would race them.
		history.replaceState(withScrollAnchorState(history.state, stateKey, pos), '');
	}

	function apply(s: ScrollAnchorState): void {
		if (userMoved) return; // they started scrolling — theirs wins
		scroller.scrollTop = resolveScrollTop(s, {
			currentTop: scroller.scrollTop,
			maxTop: scroller.scrollHeight - scroller.clientHeight,
			viewportTop: scroller.getBoundingClientRect().top,
			anchorTop: anchorTop(s.id),
		});
	}

	/**
	 * Synchronously FIRST, then again after layout. The synchronous attempt is what stops the jump:
	 * afterLayout is a double rAF, so on its own it guarantees a painted frame at the top of the list
	 * before the scroll lands, and that frame IS the jump the user sees.
	 *
	 * Applying twice is free because resolveScrollTop recomputes from where the anchor actually is
	 * (pinned idempotent in scroll-anchor.test.ts) — the second pass is a no-op when the first was right.
	 *
	 * It is not always right: with a filter or sort active, the grid applies is-hidden/order in a
	 * microtask that has not run yet, so the first attempt measures the unfiltered layout and the
	 * second corrects it one frame later. Unfiltered — the common case — lands before the first paint.
	 */
	function restore(): void {
		if (!saved || userMoved) return;
		const s = saved;
		apply(s);
		afterLayout(() => apply(s));
	}

	window.addEventListener('pagehide', capture);
	document.addEventListener('visibilitychange', onVisibility);
	// Deliberately NO beforeunload: that listener alone disqualifies a page from bfcache
	// (live-recording-record.test.ts pins the same rule for /Record).
	for (const type of ['wheel', 'touchstart', 'pointerdown'] as const) {
		scroller.addEventListener(type, markMoved, { capture: true, passive: true });
	}
	// Keyboard scrolling targets <body> when nothing inside the scroller has focus, so it never
	// reaches the scroller — this one has to be on the window.
	window.addEventListener('keydown', markMoved, { capture: true, passive: true });

	return {
		capture,
		restore,
		destroy(): void {
			window.removeEventListener('pagehide', capture);
			document.removeEventListener('visibilitychange', onVisibility);
			for (const type of ['wheel', 'touchstart', 'pointerdown'] as const) {
				scroller.removeEventListener(type, markMoved, { capture: true });
			}
			window.removeEventListener('keydown', markMoved, { capture: true });
		},
	};
}
