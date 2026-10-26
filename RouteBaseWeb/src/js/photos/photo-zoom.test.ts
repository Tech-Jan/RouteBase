import { describe, it, expect, vi, afterEach } from 'vitest';
import { mountPhotoZoom } from './photo-zoom';

// The bug this module exists for: a pinch inside the photo viewer was the BROWSER's, so it zoomed the
// whole page — close the viewer and the map behind it was still magnified, markers and all. So the
// tests that matter are (a) the viewer scales the photo itself, and (b) it refuses the native gesture
// on both engines. The transform maths are pinned in photo-zoom-math.test.ts.

const VIEW = { width: 400, height: 300 };
const IMG = { width: 400, height: 300 };

/** Model the transformed box the way the browser would: untransformed centre at the view's centre. */
function rectOf(image: HTMLImageElement): DOMRect {
	const m = /translate\((-?[\d.]+)px, (-?[\d.]+)px\) scale\(([\d.]+)\)/.exec(image.style.transform);
	const [x, y, s] = m ? [Number(m[1]), Number(m[2]), Number(m[3])] : [0, 0, 1];
	const cx = VIEW.width / 2 + x, cy = VIEW.height / 2 + y;
	const w = IMG.width * s, h = IMG.height * s;
	return { left: cx - w / 2, top: cy - h / 2, width: w, height: h, right: cx + w / 2, bottom: cy + h / 2, x: cx - w / 2, y: cy - h / 2, toJSON: () => ({}) } as DOMRect;
}

function mount() {
	const surface = document.createElement('div');
	const wrap = document.createElement('div');
	const image = document.createElement('img');
	wrap.appendChild(image);
	surface.appendChild(wrap);
	document.body.appendChild(surface);

	Object.defineProperty(image, 'offsetWidth', { value: IMG.width, configurable: true });
	Object.defineProperty(image, 'offsetHeight', { value: IMG.height, configurable: true });
	Object.defineProperty(wrap, 'clientWidth', { value: VIEW.width, configurable: true });
	Object.defineProperty(wrap, 'clientHeight', { value: VIEW.height, configurable: true });
	image.getBoundingClientRect = () => rectOf(image);

	const zoom = mountPhotoZoom({ image, surface });
	const dismiss = vi.fn();
	surface.addEventListener('click', dismiss); // the host's "click on the backdrop = close"
	return { surface, image, zoom, dismiss };
}

/** Pointer events carry a pointerId, which jsdom's MouseEvent does not — attach it by hand. */
function pointer(target: EventTarget, type: string, id: number, x: number, y: number): void {
	const e = new MouseEvent(type, { bubbles: true, cancelable: true, clientX: x, clientY: y });
	Object.defineProperty(e, 'pointerId', { value: id });
	target.dispatchEvent(e);
}

function touchMove(surface: HTMLElement, fingers: number): Event {
	const e = new Event('touchmove', { bubbles: true, cancelable: true });
	Object.defineProperty(e, 'touches', { value: Array.from({ length: fingers }, () => ({})) });
	surface.dispatchEvent(e);
	return e;
}

/** Pinch out from ±50px around the image centre to `dist` apart, and let go. */
function pinchTo(image: HTMLElement, dist: number): void {
	pointer(image, 'pointerdown', 1, 150, 150);
	pointer(image, 'pointerdown', 2, 250, 150);
	pointer(image, 'pointermove', 2, 200 + dist / 2, 150);
	pointer(image, 'pointermove', 1, 200 - dist / 2, 150);
	pointer(image, 'pointerup', 1, 200 - dist / 2, 150);
	pointer(image, 'pointerup', 2, 200 + dist / 2, 150);
}

function doubleTap(image: HTMLElement, x = 200, y = 150): void {
	for (let i = 0; i < 2; i++) {
		pointer(image, 'pointerdown', 1, x, y);
		pointer(image, 'pointerup', 1, x, y);
	}
}

afterEach(() => {
	document.body.innerHTML = '';
	vi.useRealTimers();
});

// The refusal itself is core/claim-pinch-gesture.ts and is pinned in full there. What has to stay
// pinned HERE is that the viewer actually wires it onto its own surface, and releases it on destroy —
// a refactor that dropped the call would leave the helper's tests green and the viewer broken.
describe('photo zoom — claiming the gesture from the browser', () => {
	it('refuses the native page pinch on the viewer surface', () => {
		const { surface } = mount();
		const e = new Event('gesturestart', { bubbles: true, cancelable: true });
		surface.dispatchEvent(e);
		expect(e.defaultPrevented).toBe(true);
		expect(touchMove(surface, 2).defaultPrevented).toBe(true);
		expect(touchMove(surface, 1).defaultPrevented).toBe(false); // /Photos lightbox still scrolls
	});

	it('hands the page back its gestures on destroy', () => {
		const { surface, image, zoom } = mount();
		zoom.destroy();

		const e = new Event('gesturestart', { bubbles: true, cancelable: true });
		surface.dispatchEvent(e);
		expect(e.defaultPrevented).toBe(false);
		expect(touchMove(surface, 2).defaultPrevented).toBe(false);
		expect(image.classList.contains('photo-zoomable')).toBe(false);
	});
});

describe('photo zoom — pinch', () => {
	it('scales the photo itself, tracking the spread and the midpoint', () => {
		const { image, zoom } = mount();

		pointer(image, 'pointerdown', 1, 150, 150);
		pointer(image, 'pointerdown', 2, 250, 150); // 100px apart, midpoint on the centre
		pointer(image, 'pointermove', 2, 300, 150); // 150px apart, midpoint 25px right

		expect(image.style.transform).toBe('translate(25px, 0px) scale(1.5)');
		expect(image.classList.contains('is-zoomed')).toBe(true);
		expect(zoom.isZoomed()).toBe(true);
	});

	it('squeezing back down lands exactly at rest, with no inline transform left behind', () => {
		const { image, zoom } = mount();
		pinchTo(image, 300);
		expect(zoom.isZoomed()).toBe(true);

		pinchTo(image, 20); // squeezing to a fifth of the spread, from 3× — past 1×, so held at 1×

		expect(zoom.isZoomed()).toBe(false);
		expect(image.style.transform).toBe('');
		expect(image.classList.contains('is-zoomed')).toBe(false);
	});

	it('is not started by a press on a control', () => {
		const { surface, image } = mount();
		const btn = document.createElement('button');
		surface.appendChild(btn);

		pointer(btn, 'pointerdown', 1, 150, 150);
		pointer(image, 'pointerdown', 2, 250, 150);
		pointer(image, 'pointermove', 2, 300, 150);

		expect(image.style.transform).toBe('');
	});
});

describe('photo zoom — pan', () => {
	it('ignores a one-finger drag at rest, so a swipe is never mistaken for a pan', () => {
		const { image } = mount();

		pointer(image, 'pointerdown', 1, 200, 150);
		pointer(image, 'pointermove', 1, 60, 150);

		expect(image.style.transform).toBe('');
	});

	it('drags the zoomed photo, and stops at its own edge', () => {
		const { image } = mount();
		doubleTap(image); // 2.5× about the centre

		pointer(image, 'pointerdown', 1, 200, 150);
		pointer(image, 'pointermove', 1, 150, 150);
		expect(image.style.transform).toBe('translate(-50px, 0px) scale(2.5)');

		// Overhang is (400 × 2.5 − 400) / 2 = 300 — a wild drag can't push the photo past it.
		pointer(image, 'pointermove', 1, -2000, 150);
		expect(image.style.transform).toBe('translate(-300px, 0px) scale(2.5)');
	});

	it('swallows the click that ends a pan — the host would read it as a dismiss', () => {
		const { surface, image, dismiss } = mount();
		doubleTap(image);

		pointer(image, 'pointerdown', 1, 200, 150);
		pointer(image, 'pointermove', 1, 150, 150);
		pointer(image, 'pointerup', 1, 150, 150);
		surface.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
		expect(dismiss).not.toHaveBeenCalled();

		// One click only: a deliberate tap on the backdrop afterwards still closes the viewer.
		surface.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
		expect(dismiss).toHaveBeenCalledTimes(1);
	});

	it('does not eat the first control tap after a pinch fired no trailing click (FE-01)', () => {
		const { surface, image } = mount();
		const btn = document.createElement('button');
		surface.appendChild(btn);
		const btnClick = vi.fn();
		btn.addEventListener('click', btnClick);

		// A pinch moves, so it arms the swallow guard — but a real touch pinch produces NO trailing click,
		// which jsdom also does not synthesize. The guard is left armed.
		pinchTo(image, 300);

		// The user now taps the Close button. Its press must disarm the guard so its click is not swallowed.
		pointer(btn, 'pointerdown', 1, 10, 10);
		const click = new MouseEvent('click', { bubbles: true, cancelable: true });
		btn.dispatchEvent(click);

		expect(click.defaultPrevented).toBe(false);
		expect(btnClick).toHaveBeenCalledTimes(1);
	});
});

describe('photo zoom — double-tap and reset', () => {
	it('toggles zoom on a double-tap, and does not dismiss on the second tap', () => {
		const { surface, image, zoom, dismiss } = mount();

		doubleTap(image);
		expect(zoom.isZoomed()).toBe(true);
		surface.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
		expect(dismiss).not.toHaveBeenCalled();

		doubleTap(image);
		expect(zoom.isZoomed()).toBe(false);
	});

	it('treats two slow taps as two taps', () => {
		vi.useFakeTimers();
		const { image, zoom } = mount();

		pointer(image, 'pointerdown', 1, 200, 150);
		pointer(image, 'pointerup', 1, 200, 150);
		vi.advanceTimersByTime(600);
		pointer(image, 'pointerdown', 1, 200, 150);
		pointer(image, 'pointerup', 1, 200, 150);

		expect(zoom.isZoomed()).toBe(false);
	});

	it('resets on the host paging to another photo — zoom never carries over', () => {
		const { image, zoom } = mount();
		pinchTo(image, 300);

		zoom.reset();

		expect(zoom.isZoomed()).toBe(false);
		expect(image.style.transform).toBe('');
		expect(image.classList.contains('is-zoomed')).toBe(false);
	});
});
