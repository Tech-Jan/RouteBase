import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { observeWhenSettled } from './settled-observer';
import { must } from './invariant';

// Drive intersection manually via a stubbed observer.
type Entry = { target: Element; isIntersecting: boolean };
let ioCb: (entries: Entry[]) => void;
let ioUnobserved: Element[];

class MockIntersectionObserver {
	constructor(cb: (entries: Entry[]) => void) { ioCb = cb; }
	observe(): void {}
	unobserve(el: Element): void { ioUnobserved.push(el); }
	disconnect(): void {}
}

const enter = (el: Element): void => ioCb([{ target: el, isIntersecting: true }]);
const leave = (el: Element): void => ioCb([{ target: el, isIntersecting: false }]);
const scroll = (): void => { window.dispatchEvent(new Event('scroll')); };

describe('observeWhenSettled', () => {
	let els: HTMLElement[];

	beforeEach(() => {
		vi.useFakeTimers();
		ioUnobserved = [];
		vi.stubGlobal('IntersectionObserver', MockIntersectionObserver);
		document.body.innerHTML = '<div id="a"></div><div id="b"></div>';
		els = Array.from(document.querySelectorAll<HTMLElement>('div'));
	});
	afterEach(() => {
		vi.unstubAllGlobals();
		vi.useRealTimers();
	});

	it('instant: a card that intersects while not scrolling activates at once', () => {
		const onActivate = vi.fn();
		observeWhenSettled({ elements: els, onActivate });

		enter(must(els[0], 'fixture el 0'));
		enter(must(els[1], 'fixture el 1'));

		expect(onActivate).toHaveBeenCalledTimes(2);
		expect(ioUnobserved).toEqual(els);
	});

	it('stop (idleMs): cards deferred while scrolling load once scroll goes quiet', () => {
		const onActivate = vi.fn();
		observeWhenSettled({ elements: els, onActivate, idleMs: 50, dwellMs: 10000 });

		scroll(); // now scrolling → intersect defers instead of instant
		enter(must(els[0], 'fixture el 0'));
		vi.advanceTimersByTime(49);
		expect(onActivate).not.toHaveBeenCalled();

		vi.advanceTimersByTime(1); // idle gap elapses, no further scroll
		expect(onActivate).toHaveBeenCalledTimes(1);
		expect(onActivate).toHaveBeenCalledWith(els[0]);
	});

	it('continuous scroll (dwellMs): a card held in view mid-scroll loads without a stop', () => {
		const onActivate = vi.fn();
		// idleMs high so the "stop" path can't fire — isolate the dwell backstop.
		observeWhenSettled({ elements: els, onActivate, idleMs: 10000, dwellMs: 500 });

		scroll();
		enter(must(els[0], 'fixture el 0')); // still scrolling (idle far off), stays visible
		vi.advanceTimersByTime(499);
		expect(onActivate).not.toHaveBeenCalled();

		vi.advanceTimersByTime(1);
		expect(onActivate).toHaveBeenCalledTimes(1);
	});

	it('fling: a card crossed and gone before either timer never activates', () => {
		const onActivate = vi.fn();
		observeWhenSettled({ elements: els, onActivate, idleMs: 50, dwellMs: 500 });

		scroll();
		enter(must(els[0], 'fixture el 0'));
		vi.advanceTimersByTime(25); // ~fling per-card dwell
		leave(must(els[0], 'fixture el 0')); // gone before the 500ms dwell; not in view when idle flushes at 50ms
		vi.advanceTimersByTime(1000);

		expect(onActivate).not.toHaveBeenCalled();
	});

	it('activates a card at most once', () => {
		const onActivate = vi.fn();
		observeWhenSettled({ elements: els, onActivate });

		enter(must(els[0], 'fixture el 0')); // instant
		enter(must(els[0], 'fixture el 0')); // already activated + unobserved

		expect(onActivate).toHaveBeenCalledTimes(1);
	});

	it('disconnect cancels pending timers and the scroll listener', () => {
		const onActivate = vi.fn();
		const disconnect = observeWhenSettled({ elements: els, onActivate, idleMs: 50, dwellMs: 500 });

		scroll();
		enter(must(els[0], 'fixture el 0'));
		disconnect();
		vi.advanceTimersByTime(1000);
		scroll(); // listener removed — no effect

		expect(onActivate).not.toHaveBeenCalled();
	});
});
