import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createReplayChartDrawer } from './replay-chart-drawer';
import { devicePrefKeys, getBool, listDevicePrefKeys } from './device-prefs';

/**
 * The drawer's contract is mostly about memory and about telling a tap from a drag — the two things a
 * user notices immediately when they are wrong. "Default OPEN, then remember" is the decision this pins:
 * a viewer who has never touched it sees the charts.
 */

const KEY = 'testDrawer';
const OPEN_KEY = devicePrefKeys.drawerOpen(KEY);

/** Read back through the registry — the stored NAME is device-prefs' business, not this test's. */
const storedOpen = (fallback: boolean): boolean => getBool(OPEN_KEY, fallback);
const nothingStored = (): boolean => !listDevicePrefKeys().includes(OPEN_KEY);

function mount(opts: Partial<Parameters<typeof createReplayChartDrawer>[0]> = {}) {
	document.body.innerHTML = `
		<div id="host"><div id="handle" tabindex="0"></div><div id="content"></div></div>`;
	const hostEl = document.getElementById('host')!;
	const contentEl = document.getElementById('content')!;
	const handleEl = document.getElementById('handle')!;
	// jsdom has no pointer capture and no layout.
	handleEl.setPointerCapture = vi.fn();
	handleEl.hasPointerCapture = vi.fn(() => true);
	handleEl.releasePointerCapture = vi.fn();

	const drawer = createReplayChartDrawer({
		hostEl, contentEl, handleEl, storageKey: KEY, defaultHeightPx: 200, ...opts,
	});
	return { drawer, hostEl, handleEl };
}

const press = (el: HTMLElement, y: number) =>
	el.dispatchEvent(new MouseEvent('pointerdown', { clientY: y, bubbles: true }));
const move = (el: HTMLElement, y: number) =>
	el.dispatchEvent(new MouseEvent('pointermove', { clientY: y, bubbles: true }));
const release = (el: HTMLElement, y: number) =>
	el.dispatchEvent(new MouseEvent('pointerup', { clientY: y, bubbles: true }));

describe('createReplayChartDrawer', () => {
	beforeEach(() => {
		localStorage.clear();
		document.body.innerHTML = '';
	});

	it('opens by default when nothing is remembered — the charts are the point of the strip', () => {
		const { drawer, hostEl } = mount();

		expect(drawer.isOpen()).toBe(true);
		expect(hostEl.classList.contains('is-open')).toBe(true);
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('200px');
		// Opening by default must not WRITE that default: only a real choice is remembered.
		expect(nothingStored()).toBe(true);
	});

	it('remembers being closed, and being opened again', () => {
		const first = mount();
		first.drawer.close();
		expect(storedOpen(true)).toBe(false);

		const second = mount();
		expect(second.drawer.isOpen()).toBe(false);
		expect(second.hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('0px');

		second.drawer.open();
		expect(storedOpen(false)).toBe(true);
		expect(mount().drawer.isOpen()).toBe(true);
	});

	it('a tap on the handle toggles; a drag resizes and does NOT toggle', () => {
		const { drawer, handleEl } = mount();

		// A tap: pressed and released within the slop.
		press(handleEl, 400);
		move(handleEl, 403);
		release(handleEl, 403);
		expect(drawer.isOpen()).toBe(false);
		expect(drawer.height()).toBe(0);

		// A drag: opens it (pulling the handle up asks for the charts back) and sets the height.
		press(handleEl, 400);
		move(handleEl, 250);        // 150 px up, from a closed (0 px) drawer
		release(handleEl, 250);
		expect(drawer.isOpen()).toBe(true);
		expect(drawer.height()).toBe(150);
	});

	it('dragging UP grows the drawer, and the height survives a reload', () => {
		const { drawer, handleEl, hostEl } = mount();

		press(handleEl, 500);
		move(handleEl, 400);        // 100 px up
		release(handleEl, 400);

		expect(drawer.height()).toBe(300);
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('300px');

		expect(mount().drawer.height()).toBe(300);
	});

	it('clamps to the viewport, and a modest overshoot below the minimum still clamps', () => {
		const { drawer, handleEl } = mount({ minHeightPx: 80, maxHeightPx: () => 400 });

		press(handleEl, 500);
		move(handleEl, -5000);      // far past the top of the screen
		release(handleEl, -5000);
		expect(drawer.height()).toBe(400);

		// 400 → 60 px: under the 80 px minimum but above the dismissal line, so it is still a resize.
		press(handleEl, 100);
		move(handleEl, 440);
		release(handleEl, 440);
		expect(drawer.isOpen()).toBe(true);
		expect(drawer.height()).toBe(80);
	});

	// The gesture that opens it must be able to close it — a panel you can only shrink is the thing every
	// bottom sheet gets wrong.
	it('pulling all the way down dismisses it, and remembers the size for next time', () => {
		const { drawer, handleEl, hostEl } = mount({ minHeightPx: 80 });

		press(handleEl, 500);
		move(handleEl, 400);        // grow it to 300 first, so the restored size is not the default
		release(handleEl, 400);
		expect(drawer.height()).toBe(300);

		press(handleEl, 400);
		move(handleEl, 900);        // ...and now pull it right down
		release(handleEl, 900);

		expect(drawer.isOpen()).toBe(false);
		expect(drawer.height()).toBe(0);
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('0px');
		// A drag-dismissal is a choice, and choices are remembered.
		expect(storedOpen(true)).toBe(false);

		// Re-opening restores the 300 the drag chose, not the default — the dismissal did not forget it.
		drawer.open();
		expect(drawer.height()).toBe(300);
	});

	// The drag is a PREVIEW and the release is the decision (it used to commit mid-gesture, which is what
	// made the close a flick: the strip clamped at the minimum, then jumped to nothing). So mid-drag the
	// only thing that moves is the height, continuously, and it can go below the minimum all the way to 0.
	it('compresses with the finger, below the minimum and down to nothing, without committing', () => {
		const { drawer, hostEl, handleEl } = mount({ minHeightPx: 80 });

		press(handleEl, 400);
		move(handleEl, 480);        // 80 px down from 200 → 120
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('120px');

		move(handleEl, 560);        // ...to 40, UNDER the 80 px minimum: it keeps compressing
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('40px');

		move(handleEl, 900);        // ...and bottoms out at nothing rather than a negative height
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('0px');
		expect(drawer.isOpen()).toBe(true);          // nothing is decided until the finger lifts
		expect(nothingStored()).toBe(true);
		// ...but the class previews it, so the grip widens while you can still change your mind.
		expect(hostEl.classList.contains('is-open')).toBe(false);
	});

	it('dragging back up during the same gesture brings it straight back', () => {
		const { drawer, handleEl } = mount({ minHeightPx: 80 });

		press(handleEl, 400);
		move(handleEl, 900);        // squeezed to nothing mid-drag
		move(handleEl, 200);        // ...changed your mind, still holding

		// 200 px up from where the press started, on top of the 200 px it already was.
		release(handleEl, 200);
		expect(drawer.isOpen()).toBe(true);
		expect(drawer.height()).toBe(400);
		expect(storedOpen(false)).toBe(true);
	});

	// Letting go short of the dismissal line undoes the pull instead of leaving a sliver of chart.
	it('eases back open when the pull did not go far enough', () => {
		const { drawer, hostEl, handleEl } = mount({ minHeightPx: 80 });

		press(handleEl, 400);
		move(handleEl, 550);        // 200 → 50: under the minimum, above the 40 px dismissal line
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('50px');

		release(handleEl, 550);
		expect(drawer.isOpen()).toBe(true);
		expect(drawer.height()).toBe(80);
		// The transition does the settling, so the class must be off before the height lands.
		expect(hostEl.classList.contains('is-resizing')).toBe(false);
		expect(hostEl.style.getPropertyValue('--replay-drawer-h')).toBe('80px');
	});

	it('reports the new size to the page only once the transition has settled', () => {
		vi.useFakeTimers();
		const onResize = vi.fn();
		const { drawer } = mount({ onResize });

		drawer.close();
		expect(onResize).not.toHaveBeenCalled();   // mid-slide the map would re-fit to the wrong box

		vi.advanceTimersByTime(400);
		expect(onResize).toHaveBeenCalledTimes(1);
		vi.useRealTimers();
	});

	it('the handle answers the keyboard, and carries its state for a screen reader', () => {
		const { drawer, handleEl } = mount();
		expect(handleEl.getAttribute('aria-expanded')).toBe('true');

		handleEl.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));
		expect(drawer.isOpen()).toBe(false);
		expect(handleEl.getAttribute('aria-expanded')).toBe('false');

		handleEl.dispatchEvent(new KeyboardEvent('keydown', { key: ' ', bubbles: true }));
		expect(drawer.isOpen()).toBe(true);
	});

	it('defaultOpen:false is honoured when nothing is remembered', () => {
		const { drawer } = mount({ defaultOpen: false });
		expect(drawer.isOpen()).toBe(false);
	});

	it('writes its height where the stylesheet asks for it', () => {
		const target = document.createElement('div');
		document.body.appendChild(target);
		mount({ heightVar: '--seg-drawer-h', varTarget: target });

		expect(target.style.getPropertyValue('--seg-drawer-h')).toBe('200px');
	});

	it('destroy releases the handle', () => {
		const { drawer, handleEl } = mount();
		drawer.destroy();

		press(handleEl, 400);
		release(handleEl, 400);
		expect(drawer.isOpen()).toBe(true);   // still open: the tap never reached it
	});
});
