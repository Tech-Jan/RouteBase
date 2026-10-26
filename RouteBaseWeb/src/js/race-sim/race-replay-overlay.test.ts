import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { must } from '../core/invariant';
import { createRaceReplayOverlay } from './race-replay-overlay';
import type { SidebarRow } from './race-sidebar-rows';

// First mirror test for race-replay-overlay.ts — the race replay's chrome (fullscreen toggle + Esc/Back,
// the virtualized follow sidebar, tap-to-follow) and, most importantly, the replay follow fix: the
// overlay is recreated per mount and each recreate used to stack another global Escape/Back handler,
// because createReplayHistorySession had no teardown. destroy() must now drop them.

function seedDom(): void {
	document.body.innerHTML = `
		<div id="raceHero">
			<button id="raceMapFullscreen"></button>
			<button id="raceReplayExit"></button>
			<div id="raceReplaySidebar">
				<div id="raceReplaySidebarHeader"><button id="raceReplayFollowToggle"></button></div>
				<div id="raceReplaySidebarPinned"></div>
				<div id="raceReplaySidebarBody"></div>
				<div id="raceReplaySidebarResize"></div>
			</div>
		</div>`;
	(window as unknown as { matchMedia: (q: string) => { matches: boolean } }).matchMedia = () => ({ matches: false });
}

const ROWS: SidebarRow[] = [
	{ index: 0, position: 1, name: 'PILZ Lia', gapMetres: 0, isReal: true, emphasised: false, calloutOff: false, stopped: false, status: 'finished', positionFrozen: false, statusOrdinal: null },
	{ index: 1, position: 2, name: 'Rider 2', gapMetres: 120, isReal: false, emphasised: false, calloutOff: false, stopped: false, status: 'finished', positionFrozen: false, statusOrdinal: null },
	{ index: 2, position: 3, name: 'Rider 3', gapMetres: 300, isReal: false, emphasised: false, calloutOff: false, stopped: true, status: 'finished', positionFrozen: false, statusOrdinal: null },
];

function make(over: Partial<Parameters<typeof createRaceReplayOverlay>[0]> = {}) {
	const onFollow = vi.fn();
	const onFollowToggle = vi.fn();
	const onResize = vi.fn();
	const overlay = createRaceReplayOverlay({ onFollow, onFollowToggle, onResize, ...over });
	return { overlay, onFollow, onFollowToggle, onResize };
}

const hero = (): HTMLElement => document.getElementById('raceHero')!;
const body = (): HTMLElement => document.getElementById('raceReplaySidebarBody')!;

beforeEach(seedDom);
afterEach(() => { document.body.innerHTML = ''; vi.restoreAllMocks(); });

describe('race-replay-overlay', () => {
	it('the fullscreen button toggles the hero class and fires onResize both ways', () => {
		const { overlay, onResize } = make();

		document.getElementById('raceMapFullscreen')!.dispatchEvent(new MouseEvent('click'));
		expect(hero().classList.contains('race-replay-fullscreen')).toBe(true);
		expect(document.body.classList.contains('race-replay-fullscreen-active')).toBe(true);
		expect(overlay.isFullscreen()).toBe(true);
		expect(onResize).toHaveBeenCalledTimes(1);

		document.getElementById('raceMapFullscreen')!.dispatchEvent(new MouseEvent('click'));
		expect(hero().classList.contains('race-replay-fullscreen')).toBe(false);
		expect(overlay.isFullscreen()).toBe(false);
		expect(onResize).toHaveBeenCalledTimes(2);
	});

	it('Escape exits fullscreen (the shared history session), leaving the replay running', () => {
		const { overlay } = make();
		document.getElementById('raceMapFullscreen')!.dispatchEvent(new MouseEvent('click'));
		expect(overlay.isFullscreen()).toBe(true);

		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape' }));
		expect(overlay.isFullscreen()).toBe(false);
	});

	it('destroy() removes the session Escape/Back handlers — a recreate no longer stacks them (1g)', () => {
		const docRemove = vi.spyOn(document, 'removeEventListener');
		const winRemove = vi.spyOn(window, 'removeEventListener');

		const { overlay } = make();
		overlay.destroy();

		// The session's two globals are the leak this fix closes: keydown (Esc) on document, popstate on window.
		expect(docRemove).toHaveBeenCalledWith('keydown', expect.any(Function));
		expect(winRemove).toHaveBeenCalledWith('popstate', expect.any(Function));
	});

	// The gap column swaps to a label once a rider stops. WHICH label is a separate fact: an abandoned
	// rider used to be handed the finisher's green "fin" while stalled mid-course, because the sidebar
	// had one boolean for both questions.
	it('labels a stopped finisher "fin" and a stopped non-finisher "DNF"', () => {
		const { overlay } = make();
		overlay.renderSidebar([
			{ index: 0, position: 1, name: 'Finisher', gapMetres: 0, isReal: false, emphasised: false, calloutOff: false, stopped: true, status: 'finished', positionFrozen: false, statusOrdinal: null },
			{ index: 1, position: 2, name: 'Abandoned', gapMetres: 900, isReal: false, emphasised: false, calloutOff: false, stopped: true, status: 'dnf', positionFrozen: false, statusOrdinal: null },
		], -1);

		const finisher = body().querySelector<HTMLElement>('.race-sb-row[data-index="0"]')!;
		const dnf = body().querySelector<HTMLElement>('.race-sb-row[data-index="1"]')!;

		expect(finisher.querySelector('.race-sb-gap')!.textContent).toBe('fin');
		expect(finisher.className).toContain('is-finished');

		expect(dnf.querySelector('.race-sb-gap')!.textContent).toBe('DNF');
		expect(dnf.className).toContain('is-dnf');
		expect(dnf.className).not.toContain('is-finished');
	});

	/**
	 * An abandoned rider keeps the position he HELD when he stopped, faded — otherwise his number climbs
	 * for the rest of the replay as the field rides past his parked marker. The ordinal beside the label
	 * says which number he was to drop out.
	 */
	it('draws a frozen position faded, and numbers the abandon', () => {
		const { overlay } = make();
		overlay.renderSidebar([
			{ index: 0, position: 1, name: 'Leader', gapMetres: 0, isReal: false, emphasised: false, calloutOff: false,
				stopped: false, status: 'finished', positionFrozen: false, statusOrdinal: null },
			{ index: 1, position: 12, name: 'Abandoned', gapMetres: 900, isReal: false, emphasised: false, calloutOff: false,
				stopped: true, status: 'dnf', positionFrozen: true, statusOrdinal: 2 },
		], -1);

		const riding = body().querySelector<HTMLElement>('.race-sb-row[data-index="0"] .race-sb-pos')!;
		const quit = body().querySelector<HTMLElement>('.race-sb-row[data-index="1"]')!;

		expect(riding.className).not.toContain('is-frozen');
		expect(quit.querySelector('.race-sb-pos')!.textContent).toBe('12');
		expect(quit.querySelector('.race-sb-pos')!.className).toContain('is-frozen');
		expect(quit.querySelector('.race-sb-gap')!.textContent).toBe('DNF 2');
	});

	it('shows a gap, not a label, for a rider who is still moving', () => {
		const { overlay } = make();
		overlay.renderSidebar(ROWS, -1);

		const riding = body().querySelector<HTMLElement>('.race-sb-row[data-index="1"]')!;
		expect(riding.querySelector('.race-sb-gap')!.textContent).not.toBe('fin');
		expect(riding.className).not.toContain('is-finished');
		expect(riding.className).not.toContain('is-dnf');
	});

	it('renders the field virtualized and pins the real riders above it', () => {
		const { overlay } = make();
		overlay.renderSidebar(ROWS, -1);

		// The whole field goes into the sizer; only the real riders are also pinned.
		expect(body().querySelectorAll('.race-sb-sizer .race-sb-row').length).toBe(3);
		const pinned = document.getElementById('raceReplaySidebarPinned')!;
		const pinnedRows = pinned.querySelectorAll('.race-sb-row');
		expect(pinnedRows).toHaveLength(1);
		expect(must(pinnedRows[0], 'one pinned row').getAttribute('data-index')).toBe('0'); // the one real rider
	});

	it('does not pin a rider whose call-out the owner hid', () => {
		// The switch means "stop shouting about him" on every surface at once — a rider it silenced on the
		// map and in the speed chart cannot still be sitting above the field here.
		const { overlay } = make();
		const rows = ROWS.map((r) => (r.isReal ? { ...r, calloutOff: true } : r));

		overlay.renderSidebar(rows, -1);

		expect(body().querySelectorAll('.race-sb-sizer .race-sb-row').length).toBe(3);   // still in the field
		expect(document.getElementById('raceReplaySidebarPinned')!.querySelectorAll('.race-sb-row')).toHaveLength(0);
	});

	it('syncFollowToggle flips the (follow)/(unfollow) label', () => {
		const { overlay } = make();
		const toggle = document.getElementById('raceReplayFollowToggle')!;

		overlay.syncFollowToggle(true);
		expect(toggle.textContent).toBe('(unfollow)');
		expect(toggle.classList.contains('is-following')).toBe(true);

		overlay.syncFollowToggle(false);
		expect(toggle.textContent).toBe('(follow)');
	});

	it('a tap on a row (pointer down+up, no drag) follows that rider; the header collapse is separate', () => {
		const { overlay, onFollow, onFollowToggle } = make();
		overlay.renderSidebar(ROWS, -1);

		const row = body().querySelector<HTMLElement>('.race-sb-row[data-index="1"]')!;
		row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
		row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 6, clientY: 6 })); // within slop
		expect(onFollow).toHaveBeenCalledWith(1);

		// The follow-toggle button stops propagation, so clicking it re-follows without collapsing the list.
		document.getElementById('raceReplayFollowToggle')!.dispatchEvent(new MouseEvent('click', { bubbles: true }));
		expect(onFollowToggle).toHaveBeenCalledTimes(1);
	});

	it('a drag past the slop threshold is NOT a tap', () => {
		const { overlay, onFollow } = make();
		overlay.renderSidebar(ROWS, -1);

		const row = body().querySelector<HTMLElement>('.race-sb-row[data-index="2"]')!;
		row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 0, clientY: 0 }));
		row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 40, clientY: 0 })); // moved > 8px
		expect(onFollow).not.toHaveBeenCalled();
	});

	/**
	 * Held open. The list settles back a few seconds after a scroll, which is right when you brushed past
	 * it and wrong when you are reading it — so a click says which, and only a click elsewhere releases it.
	 */
	describe('click-to-hold', () => {
		const sidebar = (): HTMLElement => document.getElementById('raceReplaySidebar')!;
		// A full click: the latch toggles on RELEASE, so a drag (release past the slop) is not a click.
		const pressOn = (el: HTMLElement): void => {
			el.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
			window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 5 }));
		};

		it('a click on the panel holds it open and marks it', () => {
			const { overlay } = make();
			overlay.renderSidebar(ROWS, -1);

			pressOn(body());

			expect(sidebar().classList.contains('is-held')).toBe(true);
			expect(body().classList.contains('is-expanded')).toBe(true);
		});

		it('does not time out while held — the whole point of the latch', () => {
			vi.useFakeTimers();
			try {
				const { overlay } = make();
				overlay.renderSidebar(ROWS, -1);
				pressOn(body());

				vi.advanceTimersByTime(60_000);

				expect(body().classList.contains('is-expanded')).toBe(true);
			} finally {
				vi.useRealTimers();
			}
		});

		/** Clicking the map must not close what you are reading — only the panel controls the panel. */
		it('a click on the map leaves it held', () => {
			const { overlay } = make();
			overlay.renderSidebar(ROWS, -1);
			pressOn(body());

			pressOn(hero());   // the map, not the panel

			expect(sidebar().classList.contains('is-held')).toBe(true);
		});

		it('a second click on the panel releases it, and hands it back to the ordinary hold timer', () => {
			vi.useFakeTimers();
			try {
				const { overlay } = make();
				overlay.renderSidebar(ROWS, -1);
				pressOn(body());

				pressOn(body());

				expect(sidebar().classList.contains('is-held')).toBe(false);
				// Not a snap shut: still expanded, now on the timer, exactly as if a scroll had just ended.
				expect(body().classList.contains('is-expanded')).toBe(true);
				vi.advanceTimersByTime(60_000);
				expect(body().classList.contains('is-expanded')).toBe(false);
			} finally {
				vi.useRealTimers();
			}
		});

		/** A row tap is "follow him". If it also toggled, picking a second rider would drop the latch. */
		it('a tap on a row follows the rider and leaves the latch alone', () => {
			const { overlay, onFollow } = make();
			overlay.renderSidebar(ROWS, -1);
			pressOn(body());   // latched

			const row = body().querySelector<HTMLElement>('.race-sb-row[data-index="1"]')!;
			row.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
			row.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 6, clientY: 6 }));

			expect(onFollow).toHaveBeenCalledWith(1);
			expect(sidebar().classList.contains('is-held')).toBe(true);
		});

		it('collapsing from the header drops the hold — no ring around a hidden panel', () => {
			const { overlay } = make();
			overlay.renderSidebar(ROWS, -1);
			pressOn(body());

			const header = document.getElementById('raceReplaySidebarHeader')!;
			pressOn(header);
			header.dispatchEvent(new MouseEvent('click', { bubbles: true }));

			expect(sidebar().classList.contains('rs-collapsed')).toBe(true);
			expect(sidebar().classList.contains('is-held')).toBe(false);
		});

		/**
		 * Reported from the app: holding the scrollbar to scroll the list turned the highlight off, and —
		 * when it was not highlighted — the list collapsed mid-drag. A drag emits neither wheel nor
		 * touchmove, so nothing was keeping it grown and the settle-back timer ran out under the pointer.
		 */
		it('a scroll drag neither toggles the latch nor lets the list collapse under it', () => {
			vi.useFakeTimers();
			try {
				const { overlay } = make();
				overlay.renderSidebar(ROWS, -1);

				body().dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
				vi.advanceTimersByTime(60_000);   // a long, slow drag

				expect(body().classList.contains('is-expanded')).toBe(true);
				expect(sidebar().classList.contains('is-held')).toBe(false);

				// Released past the slop — a drag, so it must not toggle; the timer starts now instead.
				window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 90 }));
				expect(sidebar().classList.contains('is-held')).toBe(false);
				expect(body().classList.contains('is-expanded')).toBe(true);
				vi.advanceTimersByTime(60_000);
				expect(body().classList.contains('is-expanded')).toBe(false);
			} finally {
				vi.useRealTimers();
			}
		});

		it('a scroll drag does not release a latch that is already on', () => {
			const { overlay } = make();
			overlay.renderSidebar(ROWS, -1);
			pressOn(body());

			body().dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
			window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 90 }));

			expect(sidebar().classList.contains('is-held')).toBe(true);
		});

		it('destroy() drops the latch with everything else', () => {
			const { overlay } = make();
			overlay.renderSidebar(ROWS, -1);
			pressOn(body());
			overlay.destroy();

			expect(sidebar().classList.contains('is-held')).toBe(false);
			pressOn(body());
			expect(sidebar().classList.contains('is-held')).toBe(false);   // a dead overlay stays dead
		});
	});
});
