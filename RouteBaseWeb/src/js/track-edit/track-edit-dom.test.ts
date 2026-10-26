import { describe, it, expect, vi, afterEach } from 'vitest';
import { buildDialog } from './track-edit-dom';

// The six track-edit dialogs share buildDialog. It used to be a pure element factory with no keyboard or
// focus affordances at all: no Escape, no focus moved in, no focus restored, no role/aria-modal. These
// pin the modal a11y the primitive now ships so every dialog gets it — and pin that Escape runs the
// caller's dismiss (some dialogs clear a re-entrancy guard there), not a bare DOM removal.

afterEach(() => {
	document.body.innerHTML = '';
});

describe('buildDialog modal a11y', () => {
	it('marks the dialog as a modal and labels it by its title', () => {
		const { body } = buildDialog('Save changes');
		expect(body.getAttribute('role')).toBe('dialog');
		expect(body.getAttribute('aria-modal')).toBe('true');
		const labelledby = body.getAttribute('aria-labelledby');
		expect(labelledby).toBeTruthy();
		const heading = body.querySelector('h3')!;
		expect(heading.id).toBe(labelledby);
		expect(heading.textContent).toBe('Save changes');
	});

	it('gives each dialog a unique title id so two open dialogs do not collide', () => {
		const a = buildDialog('One');
		const b = buildDialog('Two');
		expect(a.body.querySelector('h3')!.id).not.toBe(b.body.querySelector('h3')!.id);
	});

	it('moves focus into the dialog on open', () => {
		const { body } = buildDialog('Discard edits');
		expect(document.activeElement).toBe(body);
	});

	it('restores focus to the element that opened it, on close', () => {
		const trigger = document.createElement('button');
		document.body.appendChild(trigger);
		trigger.focus();
		expect(document.activeElement).toBe(trigger);

		const dialog = buildDialog('Track changed');
		expect(document.activeElement).not.toBe(trigger);

		dialog.close();
		expect(document.activeElement).toBe(trigger);
	});

	it('closes on Escape, prevents the default, and stops the key bubbling to a window handler', () => {
		const windowEscape = vi.fn();
		const onWin = (e: KeyboardEvent): void => { if (e.key === 'Escape') windowEscape(); };
		window.addEventListener('keydown', onWin);
		try {
			buildDialog('Discard edits');
			const ev = new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true });
			document.dispatchEvent(ev);

			expect(document.querySelector('.track-edit-dialog-backdrop')).toBeNull(); // removed
			expect(ev.defaultPrevented).toBe(true);
			expect(windowEscape).not.toHaveBeenCalled(); // stopPropagation kept it off the window
		} finally {
			window.removeEventListener('keydown', onWin);
		}
	});

	it('runs the caller dismiss on Escape (not a bare close), so cancel-only cleanup fires', () => {
		const dismiss = vi.fn();
		const dialog = buildDialog('Save changes');
		dialog.onEscape(dismiss);

		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
		expect(dismiss).toHaveBeenCalledTimes(1);
	});

	it('detaches its Escape listener on close so a later Escape does not fire the stale dismiss', () => {
		const dismiss = vi.fn();
		const dialog = buildDialog('Save changes');
		dialog.onEscape(dismiss);
		dialog.close();

		document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
		expect(dismiss).not.toHaveBeenCalled();
	});
});
