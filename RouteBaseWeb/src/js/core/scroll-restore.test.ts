import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import { installFakeRaf, type FakeRaf } from './raf-test-harness';
import { createScrollRestore, type ScrollRestore } from './scroll-restore';

// jsdom has no layout engine: every getBoundingClientRect() is zeros and scrollTop is stuck at 0.
// These helpers hand-place the elements so the real geometry path runs. The math itself is pinned
// separately, without a DOM, in scroll-anchor.test.ts.

const SCROLLER_TOP = 100;
const VIEWPORT_H = 800;
const CONTENT_H = 10000;

/**
 * Place an element at a fixed position in the SCROLLER's content, and derive its viewport rect the way
 * a browser does: content position minus the scroll offset. Modelling that is not optional — the whole
 * idempotence of a second restore pass rests on a scrolled element reporting a moved rect, and a fake
 * that returns a constant rect makes the second pass double-count instead.
 */
function place(el: HTMLElement, contentTop: number, height: number): void {
	el.getBoundingClientRect = () => {
		const top = contentTop - scrollerOffset();
		return { top, bottom: top + height, height, left: 0, right: 0, width: 0, x: 0, y: top, toJSON: () => ({}) } as DOMRect;
	};
}

/** The live scroller's scrollTop, read lazily so `place()` closures see the current value. */
let scrollerOffset: () => number = () => 0;

/** A fixed viewport rect — for the scroller itself, whose own edge does not move when it scrolls. */
function fix(el: HTMLElement, top: number, height: number): void {
	el.getBoundingClientRect = () => ({ top, bottom: top + height, height, left: 0, right: 0, width: 0, x: 0, y: top, toJSON: () => ({}) }) as DOMRect;
}

function buildDom(): HTMLElement {
	document.body.innerHTML = `
		<div class="cockpit-scroll">
			<div class="activity-card" data-track-id="1"></div>
			<div class="activity-card" data-track-id="2"></div>
			<div class="activity-card" data-track-id="3"></div>
		</div>`;
	const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

	let scrollTop = 0;
	Object.defineProperty(scroller, 'scrollTop', {
		get: () => scrollTop,
		set: (v: number) => { scrollTop = v; },
		configurable: true,
	});
	Object.defineProperty(scroller, 'scrollHeight', { get: () => CONTENT_H, configurable: true });
	Object.defineProperty(scroller, 'clientHeight', { get: () => VIEWPORT_H, configurable: true });
	fix(scroller, SCROLLER_TOP, VIEWPORT_H);
	scrollerOffset = () => scrollTop;

	// Content positions, i.e. where the cards sit at scrollTop 0.
	const cards = document.querySelectorAll<HTMLElement>('.activity-card');
	place(cards[0] as HTMLElement, -260, 300);  // scrolled past
	place(cards[1] as HTMLElement, 60, 300);    // straddling the top edge
	place(cards[2] as HTMLElement, 400, 300);   // fully below
	return scroller;
}

/** Every instance built by a test, so afterEach can unhook them even when an assertion throws —
 *  a leaked pagehide listener otherwise writes history.state during a LATER test. */
const live: ScrollRestore[] = [];

function track(r: ScrollRestore): ScrollRestore {
	live.push(r);
	return r;
}

function create(): ScrollRestore {
	return track(createScrollRestore({
		scroller: buildDom(),
		stateKey: 'feedScroll',
		anchor: { itemSelector: '.activity-card[data-track-id]', idAttr: 'trackId' },
	}));
}

describe('scroll-restore', () => {
	let raf: FakeRaf;

	beforeEach(() => {
		raf = installFakeRaf();
		history.replaceState(null, '');
	});
	afterEach(() => {
		Reflect.deleteProperty(document, 'elementFromPoint'); // stubbed by the hit-test cases
		while (live.length) live.pop()?.destroy();
		raf.restore();
		history.replaceState(null, '');
		document.body.innerHTML = '';
	});

	describe('capture', () => {
		it('writes the anchor into history.state on pagehide', () => {
			const restore = create();
			document.querySelector<HTMLElement>('.cockpit-scroll')!.scrollTop = 300;

			window.dispatchEvent(new Event('pagehide'));

			// At scrollTop 300 the cards sit at -560, -240 and 100. Card 2 ends at 60, above the
			// scroller's edge at 100, so card 3 is the topmost one still in view — exactly on the edge.
			expect(history.state).toEqual({ feedScroll: { id: 3, dy: 0, top: 300 } });
			restore.destroy();
		});

		it('keeps history-state keys another module owns', () => {
			history.replaceState({ replay: { mode: 'on' } }, '');
			const restore = create();

			window.dispatchEvent(new Event('pagehide'));

			expect(history.state.replay).toEqual({ mode: 'on' });
			expect(history.state.feedScroll).toBeTruthy();
			restore.destroy();
		});

		it('records a null anchor when no item is in view — pixels only', () => {
			const restore = create();
			// Everything scrolled past: nothing is left to anchor to.
			for (const el of document.querySelectorAll<HTMLElement>('.activity-card')) place(el, -900, 300);

			window.dispatchEvent(new Event('pagehide'));

			expect(history.state.feedScroll).toEqual({ id: null, dy: 0, top: 0 });
			restore.destroy();
		});

		it('writes on visibilitychange→hidden, not on →visible', () => {
			const restore = create();
			const setVisibility = (v: string): void => {
				Object.defineProperty(document, 'visibilityState', { get: () => v, configurable: true });
			};

			setVisibility('visible');
			document.dispatchEvent(new Event('visibilitychange'));
			expect(history.state).toBeNull();

			setVisibility('hidden');
			document.dispatchEvent(new Event('visibilitychange'));
			expect(history.state.feedScroll).toBeTruthy();

			restore.destroy();
		});

		describe('hit-test fast path', () => {
			// jsdom has no hit-testing, so every other test here exercises the exhaustive scan. These two
			// stub elementFromPoint to cover the path a real browser actually takes.

			it('agrees with the scan it replaces', () => {
				// The whole justification for the fast path is that it is the same answer for less work.
				// Pin that equivalence, or the optimisation is free to drift into a different anchor.
				const restore = create();
				const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;
				scroller.scrollTop = 300;

				window.dispatchEvent(new Event('pagehide'));
				const viaScan = history.state.feedScroll;

				// At scrollTop 300 the card at content 400 sits exactly on the scroller's edge.
				const card = document.querySelectorAll<HTMLElement>('.activity-card')[2] as HTMLElement;
				document.elementFromPoint = () => card;

				window.dispatchEvent(new Event('pagehide'));

				expect(history.state.feedScroll).toEqual(viaScan);
				restore.destroy();
			});

			it('falls back to the scan when the probe lands on padding or an overlay', () => {
				const restore = create();
				document.elementFromPoint = () => document.body; // closest(itemSelector) → null
				const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;
				scroller.scrollTop = 300;

				window.dispatchEvent(new Event('pagehide'));

				expect(history.state.feedScroll).toEqual({ id: 3, dy: 0, top: 300 });
				restore.destroy();
			});
		});

		it('registers no beforeunload — that listener alone would block bfcache', () => {
			const real = window.addEventListener;
			const types: string[] = [];
			window.addEventListener = ((t: string, ...rest: unknown[]) => {
				types.push(t);
				return (real as (...a: unknown[]) => void).call(window, t, ...rest);
			}) as typeof window.addEventListener;

			const restore = create();
			window.addEventListener = real;

			expect(types).not.toContain('beforeunload');
			restore.destroy();
		});
	});

	describe('restore', () => {
		it('puts the saved anchor back under the viewport edge', () => {
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			raf.flushFrames(2); // afterLayout is a double rAF

			// Card 3 sits at 400, scroller edge at 100 → it is 300 below; it should be 20 → scroll 280.
			expect(scroller.scrollTop).toBe(280);
			restore.destroy();
		});

		it('lands before the first paint — no rAF flush needed', () => {
			// THE anti-jump guarantee: afterLayout is a double rAF, so a restore that only ran there
			// would paint one frame at the top of the feed first. That frame is the visible jump.
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			// deliberately NO raf.flushFrames() here

			expect(scroller.scrollTop).toBe(280);
			restore.destroy();
		});

		it('falls back to the pixel offset when the anchor card is gone', () => {
			history.replaceState({ feedScroll: { id: 999, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			raf.flushFrames(2);

			expect(scroller.scrollTop).toBe(5000);
			restore.destroy();
		});

		it('treats a filtered-out (zero-rect) anchor as gone', () => {
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;
			place(document.querySelectorAll<HTMLElement>('.activity-card')[2] as HTMLElement, 0, 0); // display:none

			restore.restore();
			raf.flushFrames(2);

			expect(scroller.scrollTop).toBe(5000);
			restore.destroy();
		});

		it('does nothing on a fresh history entry — the whole fresh-visit gate', () => {
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			raf.flushFrames(2);

			expect(scroller.scrollTop).toBe(0);
			restore.destroy();
		});

		it.each(['wheel', 'pointerdown', 'touchstart'])('stands down once the user has scrolled (%s)', (type) => {
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			scroller.dispatchEvent(new Event(type, { bubbles: true }));
			restore.restore();
			raf.flushFrames(2);

			expect(scroller.scrollTop).toBe(0);
			restore.destroy();
		});

		it('does not re-correct after the user takes over between the two passes', () => {
			// restore() now applies synchronously and then corrects after layout, so the contract here is
			// no longer "nothing happened" — it is that the CORRECTION stands down. Shift the anchor
			// between the passes (what the async rail does on a real load) and prove the second pass
			// declines to chase it once the user has touched the page.
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			expect(scroller.scrollTop).toBe(280); // first pass landed

			place(document.querySelectorAll<HTMLElement>('.activity-card')[2] as HTMLElement, 900, 300);
			window.dispatchEvent(new Event('keydown'));
			raf.flushFrames(2);

			expect(scroller.scrollTop).toBe(280); // unchanged — the correction stood down
			restore.destroy();
		});

		it('DOES re-correct when the content above shifts and the user has not touched it', () => {
			// The twin of the case above: this is the pass that pays for the dashboard's async right rail.
			history.replaceState({ feedScroll: { id: 3, dy: 20, top: 5000 } }, '');
			const restore = create();
			const scroller = document.querySelector<HTMLElement>('.cockpit-scroll') as HTMLElement;

			restore.restore();
			place(document.querySelectorAll<HTMLElement>('.activity-card')[2] as HTMLElement, 900, 300);
			raf.flushFrames(2);

			// Anchor moved 400→900 in content space, so the scroll follows it: 900 - 100 - 20 = 780.
			expect(scroller.scrollTop).toBe(780);
			restore.destroy();
		});
	});

	describe('pixel-only mode (no anchor) — the /Activities table window', () => {
		it('captures and restores the raw offset', () => {
			const scroller = buildDom();
			const restore = track(createScrollRestore({ scroller, stateKey: 'actTableScroll' }));
			scroller.scrollTop = 1200;

			window.dispatchEvent(new Event('pagehide'));
			expect(history.state.actTableScroll).toEqual({ id: null, dy: 0, top: 1200 });
			restore.destroy();

			// Re-enter the page on that same history entry.
			const scroller2 = buildDom();
			const restore2 = track(createScrollRestore({ scroller: scroller2, stateKey: 'actTableScroll' }));
			restore2.restore();
			raf.flushFrames(2);

			expect(scroller2.scrollTop).toBe(1200);
			restore2.destroy();
		});

		it('ignores items even when the DOM has them', () => {
			// No anchor configured → the cards present in buildDom() must not be consulted.
			const scroller = buildDom();
			const restore = track(createScrollRestore({ scroller, stateKey: 'actTableScroll' }));

			window.dispatchEvent(new Event('pagehide'));

			expect(history.state.actTableScroll.id).toBeNull();
			restore.destroy();
		});
	});

	it('two scrollers on one page keep both offsets', () => {
		// /Activities restores the page AND the table window; each merges into history.state.
		const page = buildDom();
		const table = document.createElement('div');
		document.body.appendChild(table);
		const a = track(createScrollRestore({ scroller: page, stateKey: 'actPageScroll' }));
		const b = track(createScrollRestore({ scroller: table, stateKey: 'actTableScroll' }));
		page.scrollTop = 300;

		window.dispatchEvent(new Event('pagehide'));

		expect(history.state.actPageScroll.top).toBe(300);
		expect(history.state.actTableScroll).toBeTruthy();
		a.destroy();
		b.destroy();
	});

	it('destroy() unhooks capture', () => {
		const restore = create();
		restore.destroy();

		window.dispatchEvent(new Event('pagehide'));

		expect(history.state).toBeNull();
	});
});
