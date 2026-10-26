import { describe, expect, it, vi, afterEach } from 'vitest';
import {
	delimiterLabel,
	describeFile,
	importSummary,
	initRaceImportPanel,
	perLegMappingError,
	renderColumnOptions,
	renderPreviewRows,
	renderSkippedList,
	renderSplitPickers,
	renderWarnings,
} from './race-import-panel';
import type { RaceImportPreviewDto, RaceSplitColumnDto } from './race-api';

/**
 * The parsing tests that used to live here are GONE, not deleted-and-forgotten: the browser no longer
 * parses CSV at all. Delimiter sniffing, header reading, quote handling, encoding detection and every
 * alias guess now happen once on the server, over the bytes, in the code that will do the import —
 * `RaceCsvParserTests`, `RaceCsvVocabularyTests`, `RaceCsvDecoderTests` and `RaceImportPreviewTests`.
 * Having two implementations WAS the bug class; keeping two test suites would re-create it.
 *
 * What is left here is what the browser still owns: rendering the server's answer, escaping the file's
 * own strings on the way into the DOM, and the pre-flight mapping guard.
 */

function preview(over: Partial<RaceImportPreviewDto> = {}): RaceImportPreviewDto {
	return {
		delimiter: ',',
		encoding: 'utf-8',
		header: ['bib', 'name', 'finish_time'],
		rowCount: 864,
		fields: [
			{ field: 'name', label: 'Name', required: true, hint: 'The rider or team.', guess: 'name' },
			{ field: 'finish', label: 'Finish time', required: true, hint: 'Elapsed from the start.', guess: 'finish_time' },
			{ field: 'bib', label: 'Bib', required: false, hint: 'Start number.', guess: 'bib' },
		],
		splitColumns: [],
		rows: [],
		skipped: [],
		warnings: [],
		...over,
	};
}

describe('renderColumnOptions', () => {
	it('escapes header markup — the column names come out of the user\'s file', () => {
		const html = renderColumnOptions(['<img src=x onerror=1>'], true);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});

	it('a quote in a header cannot break OUT of the value attribute', () => {
		const header = 'finish" autofocus onfocus=alert(1) x="';

		const select = document.createElement('select');
		select.innerHTML = renderColumnOptions([header], true);

		// The browser is the oracle: one option, its value is the header verbatim, and the injected
		// attribute never became an attribute.
		const option = select.querySelector('option')!;
		expect(select.querySelectorAll('option')).toHaveLength(1);
		expect(option.value).toBe(header);
		expect(option.hasAttribute('onfocus')).toBe(false);
		expect(option.hasAttribute('autofocus')).toBe(false);
	});

	/**
	 * The published total is optional under per-section times — the server says so, and the label says so —
	 * but the select was once rendered as REQUIRED, so it carried no empty option. The user could not clear
	 * it, and where no guess matched the browser left it on the FIRST header column. A relay export
	 * publishing only section times then silently mapped `Place` as the published total, and
	 * RaceLegAccumulator anchors a team's missing sections on that number: a rank of 12 finished the whole
	 * team in twelve seconds.
	 */
	it('offers "(none)" when the column is optional, so it can actually be cleared', () => {
		expect(renderColumnOptions(['Place', 'Team', 'Run', 'Kayak'], false))
			.toContain('<option value="">(none)</option>');
	});

	it('offers no empty option when the column is required', () => {
		expect(renderColumnOptions(['name', 'finish_time'], true)).not.toContain('(none)');
	});
});

describe('renderSplitPickers', () => {
	const pickers: RaceSplitColumnDto[] = [
		{ checkpointId: 2, label: 'xcski', guess: 'xcski' },
		{ checkpointId: 3, label: 'dhski', guess: 'dhski' },
		{ checkpointId: 4, label: 'Section 3 — ends at Finish', guess: null },
	];

	it('renders one picker per checkpoint the server named, keyed by checkpoint id', () => {
		const html = renderSplitPickers(pickers, ['bib', 'xcski', 'dhski']);

		expect(html).toContain('data-checkpoint-id="2"');
		expect(html).toContain('data-checkpoint-id="4"');
		expect(html).toContain('<span>xcski</span>');
		expect(html).toContain('Section 3 — ends at Finish');
	});

	it('carries only the guesses the server made — an unguessed picker stays a question', () => {
		const html = renderSplitPickers(pickers, ['bib', 'xcski', 'dhski']);
		const guesses = [...html.matchAll(/data-guess="([^"]+)"/g)].map((m) => m[1]);

		expect(guesses).toEqual(['xcski', 'dhski']);
	});

	it('escapes a hostile checkpoint label', () => {
		const html = renderSplitPickers(
			[{ checkpointId: 9, label: '<img src=x onerror=alert(1)>', guess: null }], ['a']);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});
});

describe('renderPreviewRows', () => {
	const row = (over: Partial<RaceImportPreviewDto['rows'][number]> = {}) => ({
		line: 2,
		name: 'KURZ Felix',
		bib: '4496',
		status: null,
		statusRaw: null,
		computedFinishSeconds: 9798.2,
		publishedFinishSeconds: 9798.2,
		splits: [{ checkpointId: 2, checkpointName: 'Raimartihof', elapsedSeconds: 5538.2 }],
		...over,
	});

	it('is nothing at all when the mapping produced no rows', () => {
		expect(renderPreviewRows(preview())).toBe('');
	});

	/**
	 * THE guard. No heuristic distinguishes a cumulative split column from a per-section one, and reading
	 * Black Forest as per-section chains five already-elapsed columns into 10:27:26 for a 2:43:18 race —
	 * for all 864 riders, silently. The two numbers side by side settle it in a glance.
	 */
	it('flags a computed finish that disagrees with the published one', () => {
		const html = renderPreviewRows(preview({
			rows: [row({ computedFinishSeconds: 37646, publishedFinishSeconds: 9798 })],
		}));

		expect(html).toContain('race-preview-mismatch');
		expect(html).toContain('10:27:26');   // computed
		expect(html).toContain('2:43:18');    // published
	});

	it('does not flag agreement within a minute — the two clocks round differently', () => {
		const html = renderPreviewRows(preview({
			rows: [row({ computedFinishSeconds: 9800, publishedFinishSeconds: 9798 })],
		}));

		expect(html).not.toContain('race-preview-mismatch');
	});

	it('does not call a MISSING published total a mismatch — a section-times-only file is complete', () => {
		const html = renderPreviewRows(preview({
			rows: [row({ computedFinishSeconds: 9798, publishedFinishSeconds: null })],
		}));

		expect(html).not.toContain('race-preview-mismatch');
		expect(html).toContain('—');
	});

	it('shows the file\'s own status token rather than our normalization', () => {
		const html = renderPreviewRows(preview({
			rows: [row({ status: 'unknown', statusRaw: 'DNS/DNF' })],
		}));

		expect(html).toContain('DNS/DNF');
	});

	it('escapes every cell — the whole table is the user\'s own file', () => {
		const html = renderPreviewRows(preview({
			rows: [row({ name: '<img src=x onerror=alert(1)>', statusRaw: '<b>x</b>' })],
		}));

		expect(html).not.toContain('<img');
		expect(html).not.toContain('<b>');
		expect(html).toContain('&lt;img');
	});
});

describe('renderWarnings', () => {
	it('is nothing when the mapping looks sane', () => {
		expect(renderWarnings([])).toBe('');
	});

	it('escapes the warning — it quotes the file\'s own cells', () => {
		const html = renderWarnings(['Status token(s) not recognised: <img src=x>']);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});
});

describe('describeFile', () => {
	it('names the file and its size — a 0-byte pick must be visible, not rounded away', () => {
		expect(describeFile(new File(['x'], 'results.csv'))).toBe('results.csv (1 KB)');
		expect(describeFile(new File([new Uint8Array(153_084)], 'black-forest.csv')))
			.toBe('black-forest.csv (149 KB)');
	});
});

describe('delimiterLabel', () => {
	it('names the invisible ones — a tab in a status line reads as nothing at all', () => {
		expect(delimiterLabel('\t')).toBe('tab');
		expect(delimiterLabel(';')).toBe('semicolon');
		expect(delimiterLabel(',')).toBe('comma');
	});
});

describe('perLegMappingError', () => {
	it('refuses the published total mapped in as a section', () => {
		const error = perLegMappingError('finish_time', { 2: 'xcski', 3: 'dhski', 4: 'finish_time' });

		expect(error).toContain('finish_time');
		expect(error).toContain('published TOTAL');
	});

	it('refuses one column mapped to two sections', () => {
		expect(perLegMappingError('finish_time', { 2: 'xcski', 3: 'xcski' })).toContain('two checkpoints');
	});

	it('passes a mapping where every section has its own column', () => {
		expect(perLegMappingError('finish_time', { 2: 'xcski', 3: 'dhski', 4: 'run' })).toBeNull();
	});

	it('passes when no total is mapped at all — the sections produce the finish themselves', () => {
		expect(perLegMappingError(null, { 2: 'xcski', 3: 'dhski' })).toBeNull();
	});
});

describe('renderSkippedList', () => {
	it('is nothing at all when no row was skipped', () => {
		expect(renderSkippedList([])).toBe('');
	});

	it('lists the reasons the server already sent — the panel used to read only their count', () => {
		const html = renderSkippedList(["line 5: 'KURZ Felix' has no usable time", 'line 9: no name']);

		expect(html).toContain('2 row(s) skipped');
		expect(html).toContain('<li>line 9: no name</li>');
	});

	it('escapes the reason — it interpolates a name out of the user\'s own file', () => {
		const html = renderSkippedList(['line 5: \'<img src=x onerror=alert(1)>\' has no usable time']);

		expect(html).not.toContain('<img');
		expect(html).toContain('&lt;img');
	});

	it('summarises the tail rather than rendering 864 identical rows', () => {
		const many = Array.from({ length: 250 }, (_, i) => `line ${i + 2}: no name`);

		const html = renderSkippedList(many);

		expect(html).toContain('250 row(s) skipped');
		expect(html).toContain('… and 150 more');
		expect(html.match(/<li>/g)).toHaveLength(101);   // 100 reasons + the tail summary
	});
});

describe('importSummary', () => {
	/**
	 * An imported rider is not a finisher.
	 *
	 * This said "Imported 508 finishers" for the Ski to Sea file, in which 87 teams never reached the
	 * line — and the replay knows they did not: it holds them at their last checkpoint rather than walking
	 * them to a finish they never made. The server has always counted finishers correctly
	 * (RaceCsvImportResult.FinisherCount); nothing read it.
	 */
	it('separates who was imported from who actually finished', () => {
		const summary = importSummary(508, 421, 0, 0);

		expect(summary).toContain('508 rider(s)');
		expect(summary).toContain('421 finished, 87 did not');
		expect(summary).not.toContain('508 finishers');
	});

	it('says nothing about DNFs when everyone finished', () => {
		const summary = importSummary(120, 120, 0, 0);

		expect(summary).toContain('120 rider(s)');
		expect(summary).not.toContain('did not');
	});

	it('still reports skipped rows and the compressed-clock teams', () => {
		const summary = importSummary(500, 490, 8, 36);

		expect(summary).toContain('8 row(s) skipped');
		expect(summary).toContain('36 team(s)');
	});
});

/**
 * Driven through the real panel, because the wiring is the part that breaks: the file is uploaded as
 * MULTIPART now, the preview round-trips on every picker change, and a rejection must surface the
 * server's message rather than the `{code,message}` envelope around it (PF-03, the two-convention trap
 * `fail()` exists to unwrap).
 */
describe('the import panel, end to end', () => {
	function buildDom(): void {
		document.body.innerHTML = `
			<div id="raceImport">
				<form id="raceImportForm">
					<input type="file" id="raceCsvFile">
					<div id="raceCsvMapping" hidden>
						<select id="csvName"></select>
						<label id="csvFinishLabel"></label>
						<select id="csvFinish"></select>
						<select id="csvBib"></select>
						<select id="csvCategory"></select>
						<select id="csvGender"></select>
						<select id="csvClub"></select>
						<select id="csvStatus"></select>
						<input type="checkbox" id="csvPerLeg">
						<div id="raceCsvSplits"></div>
						<div id="raceImportPreview"></div>
					</div>
					<button type="submit">Import</button>
				</form>
				<p id="raceImportStatus"></p>
				<div id="raceImportSkipped"></div>
			</div>`;
	}

	function attachFile(): void {
		const file = new File(['name,finish_time\nA,1:00:00\n'], 'r.csv', { type: 'text/csv' });
		const fileInput = document.getElementById('raceCsvFile') as HTMLInputElement;
		Object.defineProperty(fileInput, 'files', { value: [file], configurable: true });
		fileInput.dispatchEvent(new Event('change'));
	}

	const settle = () => new Promise((r) => setTimeout(r, 0));
	const status = () => document.getElementById('raceImportStatus')!.textContent ?? '';

	/**
	 * Only the PREVIEW calls. The panel also lists saved mappings on init — that call is deliberate (a
	 * returning user's one click is right there) and asserting on a raw call count would make every future
	 * round trip a test failure rather than a behaviour change.
	 */
	const previewCalls = (mock: { mock: { calls: unknown[] } }): [string, RequestInit][] =>
		(mock.mock.calls as [string, RequestInit][]).filter(([url]) => url.includes('/import/preview'));

	afterEach(() => { vi.unstubAllGlobals(); document.body.innerHTML = ''; });

	it('uploads the FILE, not its text, and fills the pickers from the server\'s guesses', async () => {
		buildDom();
		const fetchMock = vi.fn(async () => new Response(JSON.stringify(preview({ delimiter: ';', encoding: 'windows-1252' })),
			{ status: 200, headers: { 'Content-Type': 'application/json' } }));
		vi.stubGlobal('fetch', fetchMock);

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		const [url, init] = previewCalls(fetchMock)[0]!;
		expect(url).toContain('/import/preview');
		// Bytes, not a decoded string: only the server can tell UTF-8 from Windows-1252, and it must not
		// be handed text the browser already mangled.
		expect(init.body).toBeInstanceOf(FormData);
		expect((init.body as FormData).get('file')).toBeInstanceOf(File);
		// And no Content-Type — the browser writes it with the multipart boundary it just generated.
		expect(init.headers).toBeUndefined();

		expect((document.getElementById('csvName') as HTMLSelectElement).value).toBe('name');
		expect((document.getElementById('csvFinish') as HTMLSelectElement).value).toBe('finish_time');
		// The detected delimiter and encoding are SHOWN: "1 column found" with no explanation is what made
		// a semicolon export look like a broken file rather than a mis-read one.
		expect(status()).toContain('semicolon');
		expect(status()).toContain('windows-1252');
	});

	it('re-previews when a picker changes, and keeps the user\'s choice over the guess', async () => {
		buildDom();
		const fetchMock = vi.fn(async () => new Response(JSON.stringify(preview()),
			{ status: 200, headers: { 'Content-Type': 'application/json' } }));
		vi.stubGlobal('fetch', fetchMock);

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		const bib = document.getElementById('csvBib') as HTMLSelectElement;
		bib.value = 'name';
		bib.dispatchEvent(new Event('change', { bubbles: true }));
		await settle();

		expect(previewCalls(fetchMock)).toHaveLength(2);
		const second = previewCalls(fetchMock)[1]![1].body as FormData;
		expect(second.get('bibColumn')).toBe('name');
		// Re-rendered from the same preview, and the user's pick survives it — a picker that snaps back to
		// the guess on every keystroke is unusable.
		expect((document.getElementById('csvBib') as HTMLSelectElement).value).toBe('name');
	});

	/**
	 * The failure this test exists for: `asyncCallback` routes a rejection to console.error and nothing
	 * else, so a preview that 400s, 413s or never connects used to leave "Reading the file…" on screen
	 * FOREVER — indistinguishable from a hang, with the only evidence in devtools.
	 */
	it('says so when the PREVIEW fails, instead of sitting on "Reading…" forever', async () => {
		buildDom();
		vi.stubGlobal('fetch', vi.fn(async () =>
			new Response('{"code":"badRequest","message":"CSV has no data rows."}', {
				status: 400, headers: { 'Content-Type': 'application/json' },
			})));

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		expect(status()).toContain('Could not read');
		expect(status()).toContain('r.csv');
		expect(status()).toContain('CSV has no data rows.');
		expect(status()).not.toContain('Reading');
	});

	it('names the file it is reading, so a mis-picked one is visible at once', async () => {
		buildDom();
		// A fetch that never settles: the busy message must already be on screen.
		vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => { /* never resolves */ })));

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		expect(status()).toContain('r.csv');
		expect(document.getElementById('raceImportStatus')!.className).toContain('is-busy');
	});

	it('surfaces the server error string, not the raw JSON envelope', async () => {
		buildDom();
		vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(preview()),
			{ status: 200, headers: { 'Content-Type': 'application/json' } })));

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		// The server rejects a bad mapping with the canonical ApiError body (D32).
		vi.stubGlobal('fetch', vi.fn(async () =>
			new Response('{"code":"badRequest","message":"Column ‘name’ is required."}', {
				status: 400, headers: { 'Content-Type': 'application/json' },
			}),
		));

		document.getElementById('raceImportForm')!.dispatchEvent(new Event('submit', { cancelable: true }));
		await settle();

		expect(status()).toContain('Column ‘name’ is required.');
		expect(status()).not.toContain('{"');   // the raw JSON envelope must not leak through
	});

	it('refuses a per-section mapping that feeds the published total in as a section, before uploading', async () => {
		buildDom();
		const fetchMock = vi.fn(async () => new Response(JSON.stringify(preview({
			splitColumns: [{ checkpointId: 2, label: 'Section 1', guess: null }],
		})), { status: 200, headers: { 'Content-Type': 'application/json' } }));
		vi.stubGlobal('fetch', fetchMock);

		initRaceImportPanel(101, () => {});
		attachFile();
		await settle();

		(document.getElementById('csvPerLeg') as HTMLInputElement).checked = true;
		(document.getElementById('csvFinish') as HTMLSelectElement).value = 'finish_time';
		const split = document.querySelector<HTMLSelectElement>('select[data-checkpoint-id="2"]')!;
		split.value = 'finish_time';

		const before = fetchMock.mock.calls.length;
		document.getElementById('raceImportForm')!.dispatchEvent(new Event('submit', { cancelable: true }));
		await settle();

		expect(status()).toContain('published TOTAL');
		expect(fetchMock.mock.calls).toHaveLength(before);   // nothing was uploaded
	});
});
