import { afterEach, describe, expect, it } from 'vitest';
import { HELD_CLASS, createHoldOpenPanel } from './panel-hold-open';

/**
 * The latch every replay's follow panel shares. Both of its rules exist because the obvious alternative
 * was tried and was wrong in the hand, so both are pinned here rather than left to the two call sites.
 */
describe('createHoldOpenPanel', () => {
	afterEach(() => { document.body.innerHTML = ''; });

	function mount(): { panel: HTMLElement; row: HTMLElement; margin: HTMLElement } {
		document.body.innerHTML = `
			<div id="outside"></div>
			<div id="panel">
				<div class="margin"></div>
				<div class="row"><span class="inner">name</span></div>
			</div>`;
		return {
			panel: document.getElementById('panel')!,
			row: document.querySelector<HTMLElement>('.row')!,
			margin: document.querySelector<HTMLElement>('.margin')!,
		};
	}

	/** A full press+release with no movement — a click. */
	const press = (el: HTMLElement): void => {
		el.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
		window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 5 }));
	};

	/** Press, move past the slop, release — a scroll drag. */
	const drag = (el: HTMLElement): void => {
		el.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
		window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 90 }));
	};

	function make(panel: HTMLElement) {
		const changes: boolean[] = [];
		const hold = createHoldOpenPanel({
			panel, ignoreSelector: '.row', onChange: (keepOpen) => changes.push(keepOpen),
		});
		return { hold, changes };
	}

	it('a click on the panel latches it, and a second click releases it', () => {
		const { panel, margin } = mount();
		const { hold } = make(panel);

		press(margin);
		expect(hold.held).toBe(true);
		expect(panel.classList.contains(HELD_CLASS)).toBe(true);

		press(margin);
		expect(hold.held).toBe(false);
		expect(panel.classList.contains(HELD_CLASS)).toBe(false);
	});

	/**
	 * The rule that replaced the first cut. Releasing on an outside click meant brushing the map while
	 * reading the list closed the thing being read.
	 */
	it('a click anywhere else leaves the latch exactly as it was', () => {
		const { panel, margin } = mount();
		const { hold } = make(panel);
		press(margin);

		press(document.getElementById('outside')!);

		expect(hold.held).toBe(true);
	});

	it('does not latch from the outside either — the panel is the only control', () => {
		const { panel } = mount();
		const { hold, changes } = make(panel);

		press(document.getElementById('outside')!);

		expect(hold.held).toBe(false);
		expect(changes).toEqual([]);
	});

	/** A tap on a row already means "follow him"; toggling as well would un-latch on the second pick. */
	it('ignores clicks on a row, and on anything inside one', () => {
		const { panel, row, margin } = mount();
		const { hold } = make(panel);

		press(row);
		expect(hold.held).toBe(false);

		press(margin);                                           // latch it
		press(document.querySelector<HTMLElement>('.inner')!);    // a child of a row still counts as the row
		expect(hold.held).toBe(true);
	});

	it('release() is idempotent', () => {
		const { panel, margin } = mount();
		const { hold } = make(panel);
		press(margin);

		hold.release();
		hold.release();

		expect(hold.held).toBe(false);
	});

	it('destroy() drops the listeners and the class', () => {
		const { panel, margin } = mount();
		const { hold } = make(panel);
		press(margin);

		hold.destroy();
		press(margin);

		expect(panel.classList.contains(HELD_CLASS)).toBe(false);
		expect(hold.held).toBe(false);
	});

	/**
	 * Scrolling the list by dragging it must not turn the highlight off. This was reported from the app:
	 * "i hold the button to scroll up and down, then he unselects the follow box".
	 */
	describe('a drag is not a click', () => {
		it('a drag past the slop does not toggle the latch', () => {
			const { panel, margin } = mount();
			const { hold } = make(panel);

			drag(margin);

			expect(hold.held).toBe(false);
		});

		it('a drag does not release a latch that is already on', () => {
			const { panel, margin } = mount();
			const { hold } = make(panel);
			press(margin);

			drag(margin);

			expect(hold.held).toBe(true);
		});

		it('keepOpen stays true for the whole press, so no settle-back timer can fire mid-drag', () => {
			const { panel, margin } = mount();
			const { hold, changes } = make(panel);

			margin.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));
			expect(hold.keepOpen).toBe(true);       // ...even though nothing is latched
			expect(hold.held).toBe(false);

			window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 90 }));
			expect(hold.keepOpen).toBe(false);
			// The caller is told exactly twice: arm no timer, then arm one.
			expect(changes).toEqual([true, false]);
		});

		it('a release OUTSIDE the panel still ends the press — the pointer leaves during a thumb drag', () => {
			const { panel, margin } = mount();
			const { hold } = make(panel);
			margin.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));

			document.getElementById('outside')!
				.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 400, clientY: 400 }));

			expect(hold.keepOpen).toBe(false);
		});

		it('a cancelled press (the browser taking over the gesture) neither toggles nor sticks open', () => {
			const { panel, margin } = mount();
			const { hold } = make(panel);
			margin.dispatchEvent(new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 }));

			window.dispatchEvent(new MouseEvent('pointercancel', { bubbles: true }));

			expect(hold.held).toBe(false);
			expect(hold.keepOpen).toBe(false);
		});
	});

	/**
	 * The other half of the same report. A scrollbar arrow held down scrolls without moving the pointer,
	 * so the slop check alone would still read the release as a click.
	 */
	it('a press on the scroll bar gutter does not toggle, but does keep the panel open', () => {
		const { panel, margin } = mount();
		const { hold } = make(panel);
		// jsdom lays nothing out: state the geometry that makes this a bar press.
		Object.defineProperty(margin, 'clientWidth', { value: 100, configurable: true });
		Object.defineProperty(margin, 'clientHeight', { value: 200, configurable: true });
		Object.defineProperty(margin, 'scrollHeight', { value: 900, configurable: true });

		const down = new MouseEvent('pointerdown', { bubbles: true, clientX: 5, clientY: 5 });
		Object.defineProperty(down, 'offsetX', { value: 106 });   // past clientWidth = in the gutter
		margin.dispatchEvent(down);
		expect(hold.keepOpen).toBe(true);

		window.dispatchEvent(new MouseEvent('pointerup', { bubbles: true, clientX: 5, clientY: 5 }));
		expect(hold.held).toBe(false);
	});

	it('with no ignoreSelector every click in the panel toggles', () => {
		const { panel, row } = mount();
		const hold = createHoldOpenPanel({ panel, onChange: () => {} });

		press(row);

		expect(hold.held).toBe(true);
	});
});
