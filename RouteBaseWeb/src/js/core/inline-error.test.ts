import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { createInlineError } from './inline-error';

// The non-blocking replacement for a validation alert(): the pins here are the three properties a
// modal has for free and an inline message has to earn — it is visible, it says which field is
// wrong, and it goes away when the user fixes it.

let host: HTMLElement;
let field: HTMLInputElement;

beforeEach(() => {
	document.body.innerHTML = '<div id="host"><input id="f"></div>';
	const h = document.getElementById('host');
	const f = document.getElementById('f');
	if (!(h instanceof HTMLElement) || !(f instanceof HTMLInputElement)) throw new Error('seed failed');
	host = h;
	field = f;
});

afterEach(() => {
	document.body.innerHTML = '';
	vi.useRealTimers();
});

function messageEl(): HTMLElement | null {
	return document.querySelector('.inline-error');
}

describe('inline-error', () => {
	it('starts hidden and shows the message in place', () => {
		const err = createInlineError(host);
		expect(messageEl()?.hidden).toBe(true);

		err.show('Segment name cannot be empty.');

		expect(messageEl()?.hidden).toBe(false);
		expect(messageEl()?.textContent).toBe('Segment name cannot be empty.');
		expect(messageEl()?.parentElement).toBe(host); // in the form, not over it
	});

	it('marks its fields while it shows and unmarks them when cleared', () => {
		const err = createInlineError(host, { fields: [field] });

		err.show('bad');
		expect(field.getAttribute('aria-invalid')).toBe('true');
		expect(field.classList.contains('is-invalid')).toBe(true);

		err.clear();
		expect(field.hasAttribute('aria-invalid')).toBe(false);
		expect(field.classList.contains('is-invalid')).toBe(false);
	});

	it('clears itself when the user edits the field it is about', () => {
		const err = createInlineError(host, { fields: [field] });
		err.show('Segment name cannot be empty.');

		field.value = 'Col du Galibier';
		field.dispatchEvent(new Event('input'));

		expect(messageEl()?.hidden).toBe(true);
		expect(field.hasAttribute('aria-invalid')).toBe(false);
	});

	it('auto-hides when there is no field to fix (action errors)', () => {
		vi.useFakeTimers();
		const err = createInlineError(host, { autoHideMs: 5000 });

		err.show('Draw a segment first.');
		expect(messageEl()?.hidden).toBe(false);

		vi.advanceTimersByTime(5000);
		expect(messageEl()?.hidden).toBe(true);
	});

	it('a re-show restarts the auto-hide window rather than inheriting the old one', () => {
		vi.useFakeTimers();
		const err = createInlineError(host, { autoHideMs: 5000 });

		err.show('first');
		vi.advanceTimersByTime(4000);
		err.show('second');
		vi.advanceTimersByTime(4000); // 8s since the first show — the first timer must not fire

		expect(messageEl()?.hidden).toBe(false);
		expect(messageEl()?.textContent).toBe('second');
	});

	it("place: 'after' puts the message beside the anchor, not inside it", () => {
		createInlineError(field, { place: 'after' });
		expect(messageEl()?.previousElementSibling).toBe(field);
	});
});
