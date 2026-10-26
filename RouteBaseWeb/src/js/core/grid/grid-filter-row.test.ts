// @vitest-environment jsdom
import { describe, it, expect, vi, afterEach } from 'vitest';
import { buildCheckboxPopup, closeOpenPopups } from './grid-filter-row';
import { must } from '../invariant';

afterEach(() => {
	document.body.innerHTML = '';
	vi.restoreAllMocks();
});

function makePopup(selected = new Set<string>()): HTMLDetailsElement {
	return buildCheckboxPopup(
		['Run', 'Bike'],
		selected,
		(summary) => (summary.textContent = 'pick'),
		() => {},
	);
}

describe('buildCheckboxPopup', () => {
	it('reflects and mutates the caller-owned selection set', () => {
		const selected = new Set<string>(['Bike']);
		const details = makePopup(selected);
		document.body.appendChild(details);
		const boxes = [...details.querySelectorAll<HTMLInputElement>('input[type=checkbox]')];
		expect(boxes.map((b) => b.checked)).toEqual([false, true]);
		const first = must(boxes[0], 'first checkbox');
		first.checked = true;
		first.dispatchEvent(new Event('change'));
		expect(selected.has('Run')).toBe(true);
	});

	it('registers global listeners on open and removes them on close (toggle-driven)', () => {
		const addDoc = vi.spyOn(document, 'addEventListener');
		const removeDoc = vi.spyOn(document, 'removeEventListener');
		const addWin = vi.spyOn(window, 'addEventListener');
		const removeWin = vi.spyOn(window, 'removeEventListener');
		const details = makePopup();
		document.body.appendChild(details);

		details.open = true;
		details.dispatchEvent(new Event('toggle'));
		const clickHandler = addDoc.mock.calls.find((c) => c[0] === 'click')?.[1];
		const scrollHandler = addWin.mock.calls.find((c) => c[0] === 'scroll')?.[1];
		expect(clickHandler).toBeTruthy();
		expect(scrollHandler).toBeTruthy();

		details.open = false;
		details.dispatchEvent(new Event('toggle'));
		expect(removeDoc.mock.calls.some((c) => c[0] === 'click' && c[1] === clickHandler)).toBe(true);
		expect(removeWin.mock.calls.some((c) => c[0] === 'scroll' && c[1] === scrollHandler)).toBe(true);
		expect(removeWin.mock.calls.some((c) => c[0] === 'resize')).toBe(true);
	});
});

describe('closeOpenPopups (GR-02)', () => {
	it('closes only the open popups under the given root', () => {
		const root = document.createElement('div');
		const open = makePopup();
		const closed = makePopup();
		root.append(open, closed);
		document.body.appendChild(root);
		open.open = true;

		closeOpenPopups(root);
		expect(open.open).toBe(false);
		expect(closed.open).toBe(false);
	});

	it('ignores popups outside the root', () => {
		const root = document.createElement('div');
		document.body.appendChild(root);
		const outside = makePopup();
		document.body.appendChild(outside);
		outside.open = true;

		closeOpenPopups(root);
		expect(outside.open).toBe(true);
	});
});
