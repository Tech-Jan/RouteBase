import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';

// SO-01: the Strava background-import "Failed" branch renders status.error into
// resultsDiv.innerHTML. It must be escaped so an HTML-bearing error string is
// inert text, not parsed markup.

describe('keys-and-data _pollImportAndShowResult (SO-01)', () => {
	beforeEach(() => {
		// Stub fetch before importing the module: its top-level loadApiKeyStatus
		// IIFE calls fetch on import. ok:false makes it return early.
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false } as Response)));
		vi.useFakeTimers();
	});

	afterEach(() => {
		vi.useRealTimers();
		vi.unstubAllGlobals();
		vi.resetModules();
	});

	it('renders an HTML-bearing Failed error string as inert text, not parsed markup', async () => {
		const { _pollImportAndShowResult } = await import('./keys-and-data');

		// Progress poll resolves a Failed status carrying an XSS payload.
		(globalThis.fetch as unknown as Mock<(url: string) => Promise<unknown>>).mockImplementation((url: string) => {
			if (typeof url === 'string' && url.includes('/api/queue/progress')) {
				return Promise.resolve({
					ok: true,
					json: () => Promise.resolve({
						// $type + operationType: the real endpoint sends both (D30). The poll narrows on the
						// `$type` import SHAPE and the `import` LABEL; other op types carry no import fields (FE-03).
						$type: 'import',
						operationType: 'import',
						isRunning: false,
						phase: 'Failed',
						error: '<img src=x onerror=alert(1)>',
					}),
				} as unknown as Response);
			}
			return Promise.resolve({ ok: true } as Response); // acknowledge POST
		});

		const btn = document.createElement('button');
		const progressDiv = document.createElement('div');
		const progressBar = document.createElement('div');
		const progressText = document.createElement('div');
		const resultsDiv = document.createElement('div');

		_pollImportAndShowResult(btn, progressDiv, progressBar, progressText, resultsDiv);

		// Advance one poll tick and flush the awaited fetch/json microtasks.
		await vi.advanceTimersByTimeAsync(1000);

		const li = resultsDiv.querySelector('li');
		expect(li).not.toBeNull();
		expect(li!.textContent).toContain('<img src=x onerror=alert(1)>');
		expect(resultsDiv.querySelector('img')).toBeNull();
	});

	it('SO-05: shows a timeout message and re-enables the button after the attempt cap', async () => {
		const { _pollImportAndShowResult } = await import('./keys-and-data');

		// Progress always "running" — the loop never completes and must hit the 600-attempt cap.
		(globalThis.fetch as unknown as Mock<(url: string) => Promise<unknown>>).mockImplementation(() =>
			Promise.resolve({ ok: true, json: () => Promise.resolve({ isRunning: true }) } as unknown as Response),
		);

		const btn = document.createElement('button');
		btn.disabled = true;
		const progressDiv = document.createElement('div');
		const progressBar = document.createElement('div');
		const progressText = document.createElement('div');
		const resultsDiv = document.createElement('div');

		_pollImportAndShowResult(btn, progressDiv, progressBar, progressText, resultsDiv);

		// 601 ticks → attempts exceeds maxAttempts (600).
		await vi.advanceTimersByTimeAsync(601_000);

		expect(progressText.textContent).toContain('Still running after 10 minutes');
		expect(btn.disabled).toBe(false);
	});
});

// Fix ②: unchecking a "force" checkbox re-enabled its dependent target but did not
// clear it — a user could submit an irreversible wipe with e.g. "Photos" silently
// still checked after unchecking "Activities".
describe('keys-and-data reset-database dialog dependency checkboxes (fix ②)', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(() => Promise.resolve({ ok: false } as Response)));

		document.body.innerHTML = `
			<dialog id="resetDatabaseDialog">
				<form id="resetDatabaseForm">
					<input type="text" id="confirmationText">
					<button type="button" id="btnConfirmReset" disabled></button>
					<div id="depNotice" class="hidden"><span id="depNoticeText"></span></div>
					<button type="button" data-quick-select="photos">Photos only</button>

					<input type="checkbox" id="grpActivities" class="group-master" data-group="activities" checked>
					<div class="group-children" data-group="activities">
						<div class="reset-check-item">
							<input type="checkbox" id="chkActivities" class="child-check" data-group="activities"
								data-forces="chkGps,chkPhotos" checked>
							<label for="chkActivities"><strong>Activities</strong></label>
						</div>
						<div class="reset-check-item">
							<input type="checkbox" id="chkGps" class="child-check" data-group="activities" checked>
							<label for="chkGps"><strong>GPS Data</strong></label>
						</div>
						<div class="reset-check-item">
							<input type="checkbox" id="chkPhotos" class="child-check" data-group="activities" checked>
							<label for="chkPhotos"><strong>Photos</strong></label>
						</div>
					</div>
				</form>
			</dialog>
		`;
	});

	afterEach(() => {
		vi.unstubAllGlobals();
		vi.resetModules();
		document.body.innerHTML = '';
	});

	it('unchecking the forcing checkbox both re-enables AND clears the dependent target', async () => {
		await import('./keys-and-data');

		const chkActivities = document.getElementById('chkActivities') as HTMLInputElement;
		const chkPhotos     = document.getElementById('chkPhotos')     as HTMLInputElement;

		// Init pass force-checks + disables the dependent target while the forcer is checked.
		expect(chkPhotos.checked).toBe(true);
		expect(chkPhotos.disabled).toBe(true);

		chkActivities.checked = false;
		chkActivities.dispatchEvent(new Event('change', { bubbles: true }));

		expect(chkPhotos.disabled).toBe(false);
		expect(chkPhotos.checked).toBe(false);
	});

	it('a manually re-checked target survives later recomputes (un-force clears only once)', async () => {
		// Regression pin: the first un-force fix cleared EVERY un-forced target on EVERY recompute,
		// so a user could never select "Photos" on its own after unchecking "Activities".
		await import('./keys-and-data');

		const chkActivities = document.getElementById('chkActivities') as HTMLInputElement;
		const chkPhotos     = document.getElementById('chkPhotos')     as HTMLInputElement;

		chkActivities.checked = false;
		chkActivities.dispatchEvent(new Event('change', { bubbles: true }));
		expect(chkPhotos.checked).toBe(false); // un-forced → cleared

		chkPhotos.checked = true; // the user picks Photos deliberately
		chkPhotos.dispatchEvent(new Event('change', { bubbles: true }));

		expect(chkPhotos.checked).toBe(true);
		expect(chkPhotos.disabled).toBe(false);
	});

	it('the "Photos only" quick-select leaves exactly Photos checked', async () => {
		// Regression pin: the preset unchecks the forcer (Activities), so a clear-all-un-forced
		// recompute would immediately wipe the very box the preset just checked.
		await import('./keys-and-data');

		(document.querySelector('[data-quick-select="photos"]') as HTMLButtonElement).click();

		const chkActivities = document.getElementById('chkActivities') as HTMLInputElement;
		const chkGps        = document.getElementById('chkGps')        as HTMLInputElement;
		const chkPhotos     = document.getElementById('chkPhotos')     as HTMLInputElement;
		expect(chkPhotos.checked).toBe(true);
		expect(chkPhotos.disabled).toBe(false);
		expect(chkActivities.checked).toBe(false);
		expect(chkGps.checked).toBe(false);
	});

	it('leaves the target forced when another source still requires it', async () => {
		await import('./keys-and-data');

		// chkGps is forced by chkActivities alone here, but exercise the "still forced"
		// branch shape: re-checking chkActivities after a manual uncheck must re-force it.
		const chkGps = document.getElementById('chkGps') as HTMLInputElement;

		chkGps.checked = false;
		chkGps.dispatchEvent(new Event('change', { bubbles: true }));

		// chkActivities (still checked) re-forces it back on.
		expect(chkGps.checked).toBe(true);
		expect(chkGps.disabled).toBe(true);
	});
});
