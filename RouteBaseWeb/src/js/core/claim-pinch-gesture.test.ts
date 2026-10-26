import { describe, it, expect, afterEach } from 'vitest';
import { claimPinchGesture } from './claim-pinch-gesture';
import { makeTouchEvent } from './fake-map-harness';

// The contract this helper owns, moved here from photo-zoom.test.ts when the charts needed it too:
// a component that implements its own pinch must refuse the BROWSER's, on both engines. Miss the
// iOS half and the gesture zooms the component on Android and the whole document on iPhone.

function mount(): HTMLElement {
	const el = document.createElement('div');
	document.body.appendChild(el);
	return el;
}

/** Dispatch a touchmove with `fingers` contact points and report whether it was refused. */
function touchMove(el: HTMLElement, fingers: number): boolean {
	const e = makeTouchEvent('touchmove', Array.from({ length: fingers }, (_, i) => ({ id: i, x: i * 50, y: 0 })));
	el.dispatchEvent(e);
	return e.defaultPrevented;
}

function gesture(el: HTMLElement, type: string): boolean {
	const e = new Event(type, { bubbles: true, cancelable: true });
	el.dispatchEvent(e);
	return e.defaultPrevented;
}

afterEach(() => { document.body.innerHTML = ''; });

describe('claimPinchGesture', () => {
	it('refuses a two-finger touchmove, which is what would have zoomed the page', () => {
		const el = mount();
		claimPinchGesture(el);
		expect(touchMove(el, 2)).toBe(true);
	});

	it('leaves a one-finger touchmove alone — the host still scrolls', () => {
		const el = mount();
		claimPinchGesture(el);
		expect(touchMove(el, 1)).toBe(false);
	});

	it('refuses the iOS gesture events, which ignore touch-action', () => {
		const el = mount();
		claimPinchGesture(el);
		for (const type of ['gesturestart', 'gesturechange', 'gestureend']) {
			expect(gesture(el, type), type).toBe(true);
		}
	});

	it('hands the page back its gestures on teardown', () => {
		const el = mount();
		const release = claimPinchGesture(el);
		release();

		expect(gesture(el, 'gesturestart')).toBe(false);
		expect(touchMove(el, 2)).toBe(false);
	});

	it('hands them back when the caller aborts its own signal', () => {
		const el = mount();
		const ac = new AbortController();
		claimPinchGesture(el, { signal: ac.signal });
		ac.abort();

		expect(gesture(el, 'gesturestart')).toBe(false);
		expect(touchMove(el, 2)).toBe(false);
	});

	it('claims nothing when handed an already-aborted signal', () => {
		const el = mount();
		const ac = new AbortController();
		ac.abort();
		claimPinchGesture(el, { signal: ac.signal });

		expect(gesture(el, 'gesturestart')).toBe(false);
		expect(touchMove(el, 2)).toBe(false);
	});
});
