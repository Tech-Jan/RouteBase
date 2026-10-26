import { describe, expect, it, vi } from 'vitest';
import { initRaceClearField } from './race-clear-field';

function mount() {
	const button = document.createElement('button');
	document.body.replaceChildren(button);
	return button;
}

describe('initRaceClearField', () => {
	it('clears the field and tells the page, once confirmed', async () => {
		const button = mount();
		const onCleared = vi.fn();
		const clear = vi.fn(async () => ({ removed: 426 }));
		initRaceClearField({ raceId: 3, button, onCleared, confirm: () => true, clear });

		button.click();
		await vi.waitFor(() => expect(onCleared).toHaveBeenCalled());

		expect(clear).toHaveBeenCalledWith(3);
	});

	// The whole field, gone — a misfire is expensive, so a declined confirm must reach no API at all.
	it('does nothing when the confirm is declined', () => {
		const button = mount();
		const onCleared = vi.fn();
		const clear = vi.fn(async () => ({ removed: 0 }));
		initRaceClearField({ raceId: 3, button, onCleared, confirm: () => false, clear });

		button.click();

		expect(clear).not.toHaveBeenCalled();
		expect(onCleared).not.toHaveBeenCalled();
	});

	// Double-submit guard: the button must lock the instant the clear starts, before the write resolves —
	// a second click while the request is in flight would clear an already-empty field (or race it).
	it('disables the button immediately on click, before the clear resolves', async () => {
		const button = mount();
		let settleClear: () => void = () => {};
		const clear = vi.fn(() => new Promise<{ removed: number }>((res) => { settleClear = () => res({ removed: 5 }); }));
		const onCleared = vi.fn();
		initRaceClearField({ raceId: 3, button, onCleared, confirm: () => true, clear });

		button.click();
		// Synchronously after the click, while clear() is still pending:
		expect(button.hasAttribute('disabled')).toBe(true);
		expect(onCleared).not.toHaveBeenCalled();

		settleClear();
		await vi.waitFor(() => expect(onCleared).toHaveBeenCalled());
		expect(button.hasAttribute('disabled')).toBe(true); // stays locked — the page reloads on success
	});

	it('reports a refusal and re-enables the button', async () => {
		const button = mount();
		const onError = vi.fn();
		const clear = vi.fn(async () => { throw new Error('Not your race.'); });
		initRaceClearField({ raceId: 3, button, onCleared: () => {}, confirm: () => true, clear, onError });

		button.click();
		await vi.waitFor(() => expect(onError).toHaveBeenCalledWith('Not your race.'));

		expect(button.hasAttribute('disabled')).toBe(false);
	});
});
