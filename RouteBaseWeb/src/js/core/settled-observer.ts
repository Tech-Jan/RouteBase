// settled-observer.ts — lazy activation that fires only once a card has settled in view,
// not while the user flings past it. Shared by the dashboard minimap + photo-strip loaders.
//
// Problem: a fast scroll (measured ~25ms per-card dwell on both mobile and PC flings) drags
// every crossed card through the viewport, and a bare IntersectionObserver fires a fetch for
// each — a full-feed fling = ~1 request per card. Absolute pixel velocity is device-dependent,
// so we don't gate on it; we gate on "is the user still scrolling", via three cases:
//
//   • instant — a card that intersects while NOT scrolling loads at once (page load, or after
//     a stop). Zero latency in the common case; nothing is armed while nothing moves.
//   • stop (idleMs) — once scroll events go quiet for idleMs, load every card still in view.
//     Fling → stop → only the cards you landed on load; mid-fling nothing does.
//   • continuous scroll (dwellMs) — while still scrolling, a card held in view for dwellMs
//     loads anyway, so a slow, never-stopping drag still streams in. dwellMs ≫ fling dwell
//     (~25ms), so a fling never trips it.
//
// The two thresholds are independent and device-independent (no px/s tuning): idleMs is how
// fast we react after you stop; dwellMs is how patient we are while you keep scrolling.

import { must } from './invariant';

export interface SettledObserverOpts {
	/** Elements to observe; each `onActivate`s at most once, then is unobserved. */
	elements: ArrayLike<Element>;
	/** Called once when an element has settled into view. */
	onActivate: (el: Element) => void;
	/** IntersectionObserver look-ahead. Default '200px'. */
	rootMargin?: string;
	/** Quiet-after-scroll gap before loading in-view cards (stop latency). Default 50ms. */
	idleMs?: number;
	/** Continuous-visibility backstop while still scrolling. Default 500ms. */
	dwellMs?: number;
}

/**
 * Observe `elements` and invoke `onActivate` once each, but only once the user has settled on
 * them (see file header). Returns a disconnect fn that tears down the observer, scroll
 * listener, and any pending timers.
 */
export function observeWhenSettled(opts: SettledObserverOpts): () => void {
	const rootMargin = opts.rootMargin ?? '200px';
	const idleMs = opts.idleMs ?? 50;
	const dwellMs = opts.dwellMs ?? 500;

	const observed = new Set<Element>();
	const visible = new Set<Element>();
	const dwellTimers = new Map<Element, ReturnType<typeof setTimeout>>();
	let idleTimer: ReturnType<typeof setTimeout> | 0 = 0;
	let scrolling = false;

	function clearDwell(el: Element): void {
		const t = dwellTimers.get(el);
		if (t) { clearTimeout(t); dwellTimers.delete(el); }
	}

	function activate(el: Element): void {
		if (!observed.has(el)) return; // already activated
		observed.delete(el);
		visible.delete(el);
		clearDwell(el);
		observer.unobserve(el);
		opts.onActivate(el);
	}

	const observer = new IntersectionObserver(
		(entries) => {
			for (const entry of entries) {
				const el = entry.target;
				if (entry.isIntersecting) {
					if (!scrolling) { activate(el); continue; } // instant: settled / initial load
					visible.add(el);
					if (!dwellTimers.has(el)) {
						// Continuous-scroll backstop: load if still in view after dwellMs.
						dwellTimers.set(el, setTimeout(() => {
							dwellTimers.delete(el);
							if (visible.has(el)) activate(el);
						}, dwellMs));
					}
				} else {
					visible.delete(el);
					clearDwell(el);
				}
			}
		},
		{ root: null, rootMargin, threshold: 0 },
	);

	function onScroll(): void {
		scrolling = true;
		if (idleTimer) clearTimeout(idleTimer);
		idleTimer = setTimeout(() => {
			idleTimer = 0;
			scrolling = false;
			for (const el of Array.from(visible)) activate(el); // stop → load what's in view
		}, idleMs);
	}

	for (let i = 0; i < opts.elements.length; i++) {
		const el = must(opts.elements[i], 'i < length (loop-bounded)');
		observed.add(el);
		observer.observe(el);
	}
	if (observed.size === 0) return () => {};

	// Capture phase so we catch scrolls from the window OR any inner overflow container.
	window.addEventListener('scroll', onScroll, { capture: true, passive: true });

	return function disconnect(): void {
		window.removeEventListener('scroll', onScroll, { capture: true });
		if (idleTimer) clearTimeout(idleTimer);
		for (const t of dwellTimers.values()) clearTimeout(t);
		dwellTimers.clear();
		visible.clear();
		observed.clear();
		observer.disconnect();
	};
}
