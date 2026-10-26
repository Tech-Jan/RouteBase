import { afterEach, describe, expect, it, vi } from 'vitest';
import { createEffortListExpand } from './effort-list-expand';

/**
 * The one contract between the effort list's settle-back timer and the shared latch
 * (core/panel-hold-open): while the panel must stay open there must be NO timer.
 *
 * Its own file because effort-list-expand.test.ts runs in the node environment — it covers the pure
 * centerScrollTop half, which needs no DOM. This half needs one.
 */
describe('createEffortListExpand — staying open', () => {
	afterEach(() => { document.body.innerHTML = ''; vi.useRealTimers(); });

	function mount(stayOpen: () => boolean) {
		const container = document.createElement('div');
		document.body.appendChild(container);
		const list = createEffortListExpand({
			container,
			rowHeight: () => 0,          // no centring: this is about the timer, not the scroll
			getFollowIndex: () => -1,
			stayOpen,
		});
		return { container, expand: list.expand };
	}

	it('settles back on its own when the panel is not held', () => {
		vi.useFakeTimers();
		const { container, expand } = mount(() => false);

		expand(false);
		expect(container.classList.contains('is-expanded')).toBe(true);
		vi.advanceTimersByTime(60_000);

		expect(container.classList.contains('is-expanded')).toBe(false);
	});

	it('never settles back while the panel must stay open', () => {
		vi.useFakeTimers();
		const { container, expand } = mount(() => true);

		expand(false);
		vi.advanceTimersByTime(60_000);

		expect(container.classList.contains('is-expanded')).toBe(true);
	});

	it('re-arms the timer when the latch is released', () => {
		vi.useFakeTimers();
		let held = true;
		const { container, expand } = mount(() => held);
		expand(false);

		// What the sidebar's onChange does on release: run the grow again, now with a timer.
		held = false;
		expand(false);

		expect(container.classList.contains('is-expanded')).toBe(true);
		vi.advanceTimersByTime(60_000);
		expect(container.classList.contains('is-expanded')).toBe(false);
	});

	it('works without the predicate at all — an unlatched caller keeps the old behaviour', () => {
		vi.useFakeTimers();
		const container = document.createElement('div');
		document.body.appendChild(container);
		const { expand } = createEffortListExpand({
			container, rowHeight: () => 0, getFollowIndex: () => -1,
		});

		expand(false);
		vi.advanceTimersByTime(60_000);

		expect(container.classList.contains('is-expanded')).toBe(false);
	});
});
