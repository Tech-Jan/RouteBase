import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createRiderPanel } from './live-view-panel';
import type { WatchedSession } from './live-view-sessions';

// LV-06: the panel bound two listeners per rider row and had no way to take them off again.

const session = (sessionId: string): WatchedSession => ({
	meta: {
		sessionId, displayName: sessionId, isSelf: false, activityType: 'Ride',
		startedAt: '2026-07-12T10:00:00Z', status: 'recording', trackId: 1, pointCount: 0,
	},
	color: '#2a78d6',
	coords: [],
	count: 0,
	lastSeq: -1,
	status: 'recording',
	visible: true,
	catchupSettled: true,
	queued: [],
});

const onFollow = vi.fn();
const onToggleVisible = vi.fn();

function panel(): ReturnType<typeof createRiderPanel> {
	return createRiderPanel(
		document.getElementById('lv-people-list')!,
		document.getElementById('lv-person-template') as HTMLTemplateElement,
		{ onFollow, onToggleVisible },
	);
}

beforeEach(() => {
	vi.clearAllMocks();
	document.body.innerHTML = `
		<div id="lv-people-list"></div>
		<template id="lv-person-template">
			<div class="lv-person">
				<span class="lv-person-swatch"></span>
				<button class="lv-person-name"></button>
				<span class="lv-person-type"></span>
				<input class="lv-person-show" type="checkbox">
				<span class="lv-person-points"></span>
				<span class="lv-person-elapsed"></span>
				<span class="lv-person-status"></span>
				<a class="lv-person-activity"></a>
			</div>
		</template>
		<span id="lv-status-badge"></span><div id="lv-stopped-banner"></div>
		<a id="lv-activity-link"></a>
		<span id="lv-elapsed"></span><span id="lv-points"></span>
		<span id="lv-type"></span><span id="lv-started"></span>`;
});

afterEach(() => { document.body.innerHTML = ''; });

describe('RiderPanel teardown — LV-06', () => {
	it('dispose() removes every row listener', () => {
		const p = panel();
		for (const id of ['a', 'b', 'c']) p.addRow(session(id));
		const rows = [...document.querySelectorAll('.lv-person')];
		expect(rows).toHaveLength(3);

		p.dispose();

		for (const row of rows) {
			row.querySelector<HTMLButtonElement>('.lv-person-name')!.click();
			row.querySelector('.lv-person-show')!.dispatchEvent(new Event('change'));
		}
		expect(onFollow).not.toHaveBeenCalled();
		expect(onToggleVisible).not.toHaveBeenCalled();
	});

	it('the listeners do fire before dispose (the pin above is not vacuous)', () => {
		const p = panel();
		p.addRow(session('a'));

		document.querySelector<HTMLButtonElement>('.lv-person-name')!.click();
		document.querySelector('.lv-person-show')!.dispatchEvent(new Event('change'));

		expect(onFollow).toHaveBeenCalledWith('a');
		expect(onToggleVisible).toHaveBeenCalledWith('a', true);
	});

	it('dispose() is idempotent and leaves the panel inert', () => {
		const p = panel();
		p.addRow(session('a'));

		p.dispose();
		p.dispose();

		expect(p.hasRow('a')).toBe(false);
	});
});

describe('per-rider status badge — PI-08', () => {
	it('a refused join (status "unavailable") reads distinctly from LIVE-with-zero-points', () => {
		const p = panel();
		const s = session('a');
		s.status = 'unavailable';
		p.addRow(s);

		const badge = document.querySelector<HTMLElement>('.lv-person-status')!;
		expect(badge.textContent).toBe('UNAVAILABLE');
		expect(badge.className).toContain('lv-person-status--unavailable');
		expect(badge.title).toBe('Live view unavailable for this rider');
	});

	it('a normal LIVE row (still waiting for a GPS fix) carries no such title', () => {
		const p = panel();
		p.addRow(session('a')); // status: 'recording', count: 0 — no points yet

		const badge = document.querySelector<HTMLElement>('.lv-person-status')!;
		expect(badge.textContent).toBe('LIVE');
		expect(badge.title).toBe('');
	});
});

describe('header status badge — aria-live change-guard (item 3)', () => {
	// Count textContent writes to the badge node: renderPageStatus runs every 1 s tick, and #lv-status-badge
	// is a role=status live region, so an identical re-write would re-announce the same state every second.
	function countTextWrites(el: HTMLElement): () => number {
		let n = 0;
		const desc = Object.getOwnPropertyDescriptor(Node.prototype, 'textContent')!;
		Object.defineProperty(el, 'textContent', {
			configurable: true,
			get() { return desc.get!.call(this); },
			set(v) { n++; desc.set!.call(this, v); },
		});
		return () => n;
	}

	it('repeated identical renderPageStatus does not rewrite the badge; a changed state does', () => {
		const p = panel();
		const badge = document.getElementById('lv-status-badge')!;
		const writes = countTextWrites(badge);

		const live = [session('a')]; // status: 'recording' → LIVE
		p.renderPageStatus(live);
		expect(badge.textContent).toBe('LIVE');
		const afterFirst = writes();
		expect(afterFirst).toBe(1);

		// The 1 s tick re-runs it with the same state — the live region must stay quiet.
		p.renderPageStatus(live);
		p.renderPageStatus(live);
		expect(writes()).toBe(1);

		// A real transition repaints exactly once.
		const paused = session('b'); paused.status = 'paused';
		p.renderPageStatus([paused]);
		expect(badge.textContent).toBe('PAUSED');
		expect(writes()).toBe(2);
	});

	it('markStale updates the guard, so a later live render still repaints (no stuck STALE)', () => {
		const p = panel();
		const badge = document.getElementById('lv-status-badge')!;
		const live = [session('a')];

		p.renderPageStatus(live);
		expect(badge.textContent).toBe('LIVE');
		p.markStale();
		expect(badge.textContent).toBe('STALE');
		// Back to the SAME computed state as before STALE — must still repaint, else STALE would stick.
		p.renderPageStatus(live);
		expect(badge.textContent).toBe('LIVE');
	});
});

describe('elapsed cell — active riding time (FE-08/FE-12)', () => {
	function panelWithElapsed(elapsedOf: (s: WatchedSession) => number): ReturnType<typeof createRiderPanel> {
		return createRiderPanel(
			document.getElementById('lv-people-list')!,
			document.getElementById('lv-person-template') as HTMLTemplateElement,
			{ onFollow, onToggleVisible, elapsedOf },
		);
	}

	const cell = (): HTMLElement => document.querySelector<HTMLElement>('.lv-person-elapsed')!;

	it('renders the active duration for a recording rider (never --:--)', () => {
		const p = panelWithElapsed(() => 65);
		p.addRow(session('a'));
		expect(cell().textContent).toBe('01:05');
	});

	it('shows a frozen duration for a paused rider instead of blanking to --:--', () => {
		const p = panelWithElapsed(() => 42);
		const s = session('a');
		s.status = 'paused';
		p.addRow(s);
		expect(cell().textContent).toBe('00:42');
	});

	it('shows a final duration for an ended rider (was blank before)', () => {
		const p = panelWithElapsed(() => 3661);
		const s = session('a');
		s.status = 'stopped';
		p.addRow(s);
		expect(cell().textContent).toBe('1:01:01');
	});

	it('falls back to wall-clock when no elapsedOf is wired — still no --:-- on pause', () => {
		const p = panel(); // no elapsedOf callback
		const s = session('a');
		s.status = 'paused';
		s.meta.startedAt = new Date(Date.now() - 90_000).toISOString();
		p.addRow(s);
		expect(cell().textContent).not.toBe('--:--');
		expect(cell().textContent).toMatch(/^\d{2}:\d{2}$/);
	});
});
