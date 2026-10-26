import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';

// Pins the DC-09 fix. An EMPTY #activityDetailsConfig island used to make
// activity-details-page.ts throw at import — `JSON.parse(el.textContent ?? '')` on '' — which took the
// whole Details page down instead of falling back. It now routes through readIsland → null → the
// disabled path, and must never throw. This module self-executes on import, so importing it IS the test.
describe('activity-details-page bootstrap fallback', () => {
	let errSpy: ReturnType<typeof vi.spyOn>;

	beforeEach(() => {
		errSpy = vi.spyOn(console, 'error').mockImplementation(() => {});
	});
	afterEach(async () => {
		document.body.innerHTML = '';
		errSpy.mockRestore();
		const { vi: vitest } = await import('vitest');
		vitest.resetModules();
	});

	it('does not throw and disables interactions on an empty island', async () => {
		document.body.innerHTML = '<script type="application/json" id="activityDetailsConfig"></script>';
		await expect(import('./activity-details-page')).resolves.toBeDefined();
		expect(errSpy).toHaveBeenCalledWith(expect.stringContaining('page interactions disabled'));
	});

	it('does not throw and disables interactions on a missing island', async () => {
		// No #activityDetailsConfig at all — the other half of "absent ≠ broken".
		await expect(import('./activity-details-page')).resolves.toBeDefined();
		expect(errSpy).toHaveBeenCalledWith(expect.stringContaining('page interactions disabled'));
	});
});
