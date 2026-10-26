/**
 * The results-CSV import panel. Lives on the race page (it needs a race to import INTO), so it is a
 * module the page pulls in rather than its own entry.
 *
 * **The browser does not parse CSV.** It used to — its own tokenizer, its own comma, its own header
 * reader — and every one of those was a second implementation of something the server already did
 * differently. That divergence WAS the bug class: a delimiter sniffed on one side only, a quote rule that
 * disagreed, a leading blank line handled twice, and an encoding the browser could not even see
 * (`Blob.text()` is unconditionally UTF-8, so a Windows-1252 export lost every accented name before it
 * left the page).
 *
 * Now the file is uploaded as multipart and the server answers with a PREVIEW: the delimiter and encoding
 * it detected, the header it read, an alias-driven guess per canonical field, and the first few rows
 * fully built under the proposed mapping. This module renders that answer and sends back a mapping.
 *
 * The preview is also the only honest way to choose between the two shapes of results file:
 *
 *   elapsed (default) — each split column holds time FROM THE START. Read as-is.
 *   per-section       — each column holds that SECTION's duration, as relay and stage results publish
 *                       them. They are chained, and the sum becomes the finish.
 *
 * No heuristic can tell those apart. A computed 10:27:26 sitting next to a published 2:43:18 can.
 */

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { escapeHtml } from '../core/html';
import { formatElapsed } from '../core/format';
import { importRaceCsv, previewRaceCsv } from './race-api';
import type { RaceCsvMappingBody, RaceImportPreviewDto, RaceSplitColumnDto } from './race-api';
import { createRaceImportProfiles, profileSelections } from './race-import-profiles';

/** How long before a pending request starts saying so. See busy(). */
const SLOW_REQUEST_MS = 3000;

/** A file's size as something a person reads. The preview names it, so a 0-byte pick is visible too. */
export function describeFile(file: File): string {
	const kb = Math.max(1, Math.round(file.size / 1024));
	return `${file.name} (${kb} KB)`;
}

/** Picker element id → the canonical field it maps. The server owns the labels and the aliases. */
const FIELD_SELECTS: ReadonlyArray<readonly [id: string, field: string]> = [
	['csvName', 'name'],
	['csvFinish', 'finish'],
	['csvBib', 'bib'],
	['csvCategory', 'category'],
	['csvGender', 'gender'],
	['csvClub', 'club'],
	['csvStatus', 'status'],
	// Staggered starts. Mapping Gun AND Net derives the offset with no extra input; an explicit offset
	// column wins over both. A wave label is imported as text — the user gives it a number afterwards.
	['csvGun', 'gunTime'],
	['csvNet', 'netTime'],
	['csvStartOffset', 'startOffset'],
	['csvWave', 'wave'],
	['csvDiscipline', 'discipline'],
];

/** The id of the split picker for a checkpoint. Built from a constant + the id — never from fragments. */
export function splitSelectId(checkpointId: number): string {
	return `csvSplit-${checkpointId}`;
}

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

/**
 * The `<option>` markup for one column picker.
 *
 * Pure, and separated from the DOM, because the strings it interpolates come out of a FILE the user
 * picked: raw, that was self-XSS via a crafted CSV header, and one `"` in a column name away from a
 * silently broken picker. Both the attribute and the text are escaped.
 */
export function renderColumnOptions(header: readonly string[], required: boolean): string {
	return (
		(required ? '' : '<option value="">(none)</option>') +
		header.map((h) => `<option value="${escapeHtml(h)}">${escapeHtml(h)}</option>`).join('')
	);
}

/**
 * A split picker per checkpoint that takes a column.
 *
 * Which checkpoints those are, what each is called and which column it probably wants are all decided
 * SERVER-SIDE (see RaceImportPreview.SplitPickers) — the mode-dependent labelling is what shifted every
 * Ski to Sea column by one when the browser owned it. This renders the answer and nothing else.
 */
export function renderSplitPickers(
	pickers: readonly RaceSplitColumnDto[], header: readonly string[],
): string {
	return pickers
		.map((p) => `
			<label class="race-csv-field">
				<span>${escapeHtml(p.label)}</span>
				<select id="${splitSelectId(p.checkpointId)}" data-checkpoint-id="${p.checkpointId}">
					${renderColumnOptions(header, false)}
				</select>
				${p.guess ? `<input type="hidden" data-guess="${escapeHtml(p.guess)}">` : ''}
			</label>`)
		.join('');
}

/**
 * The one per-section mapping mistake that is silent, systematic and catastrophic — caught before the
 * upload, so it costs a click rather than a field.
 *
 * The published total is not a section. Feed it in as one and every rider's replayed finish becomes his
 * section sum PLUS the whole race total — Ski to Sea 2025 imported at 11:07 instead of 5:44. Mapping one
 * column to two sections is the same slip seen from the other side.
 *
 * Pure: the rule is the tested part, not the dialog. Returns null when the mapping is sane.
 */
export function perLegMappingError(
	finishColumn: string | null,
	splitColumns: Readonly<Record<number, string>>,
): string | null {
	const columns = Object.values(splitColumns);

	if (finishColumn && columns.includes(finishColumn)) {
		return `“${finishColumn}” is the published TOTAL, but it is also mapped as a section. Under per-section ` +
			`times each picker wants the duration of the section ENDING at that checkpoint — the last section's ` +
			`own column, not the finish time.`;
	}

	const duplicate = columns.find((c, i) => columns.indexOf(c) !== i);
	if (duplicate) {
		return `“${duplicate}” is mapped to two checkpoints. Each section has its own column — check the mapping ` +
			`is not shifted by one.`;
	}
	return null;
}

/** The delimiter as something a person can read — a tab is not visible in a status line. */
export function delimiterLabel(delimiter: string): string {
	if (delimiter === '\t') return 'tab';
	if (delimiter === ';') return 'semicolon';
	if (delimiter === ',') return 'comma';
	if (delimiter === '|') return 'pipe';
	return delimiter;
}

/**
 * The preview table: the first few rows as they would be IMPORTED, with the computed finish next to the
 * published one.
 *
 * That pairing is the whole feature. No heuristic distinguishes a cumulative split column from a per-leg
 * one, and reading a cumulative file as per-leg turns Black Forest's 2:43:18 into 10:27:26 — for all 864
 * riders, silently. Two numbers side by side settle it in a glance, and catch a column mapping shifted by
 * one and the Boston decimal-minutes trap on the way past.
 */
export function renderPreviewRows(preview: RaceImportPreviewDto): string {
	if (preview.rows.length === 0) return '';

	const head = preview.rows[0];
	const splitHeads = (head?.splits ?? []).map((s) => `<th>${escapeHtml(s.checkpointName ?? '—')}</th>`).join('');

	const body = preview.rows.map((r) => {
		const splits = r.splits.map((s) => `<td>${escapeHtml(formatElapsed(s.elapsedSeconds))}</td>`).join('');
		// Only flag a real disagreement: a missing published total is not a mismatch, it is a file that
		// publishes section times only — which is a complete file, not a broken one.
		const disagrees = r.computedFinishSeconds != null && r.publishedFinishSeconds != null
			&& Math.abs(r.computedFinishSeconds - r.publishedFinishSeconds) > 60;
		return `<tr>
			<td>${escapeHtml(String(r.line))}</td>
			<td>${escapeHtml(r.name ?? '—')}</td>
			<td>${escapeHtml(r.bib ?? '')}</td>
			${splits}
			<td class="${disagrees ? 'race-preview-mismatch' : ''}">${escapeHtml(elapsedOrDash(r.computedFinishSeconds))}</td>
			<td>${escapeHtml(elapsedOrDash(r.publishedFinishSeconds))}</td>
			<td>${escapeHtml(r.statusRaw ?? r.status ?? '')}</td>
		</tr>`;
	}).join('');

	return `<table class="race-preview-table">
		<thead><tr>
			<th>Line</th><th>Name</th><th>Bib</th>${splitHeads}
			<th>Finish (computed)</th><th>Finish (published)</th><th>Status</th>
		</tr></thead>
		<tbody>${body}</tbody>
	</table>`;
}

const elapsedOrDash = (seconds: number | null): string =>
	seconds == null ? '—' : formatElapsed(seconds);

/** The server's warnings about this mapping, if any. Escaped: they quote the file's own cells. */
export function renderWarnings(warnings: readonly string[]): string {
	if (warnings.length === 0) return '';
	return `<ul class="race-import-warnings">${
		warnings.map((w) => `<li>${escapeHtml(w)}</li>`).join('')
	}</ul>`;
}

/**
 * How many skipped rows are listed before the tail is summarised. A mis-mapped column skips EVERY row,
 * and 864 identical `<li>`s is a wall, not an explanation — the first hundred say the same thing.
 */
const SKIPPED_LIST_CAP = 100;

/**
 * The skipped rows, as a collapsible list. Closed by default: this is the detail behind the count in the
 * status line, not a second headline.
 *
 * The reasons have always been computed, line-numbered, DTO'd and sent — and the panel read only
 * `.length`, so "37 row(s) skipped" was the entire diagnosis for a mapping the user then had to guess at.
 * Every reason interpolates a NAME out of the user's own file, so both halves are escaped.
 */
export function renderSkippedList(skipped: readonly string[]): string {
	if (skipped.length === 0) return '';

	const shown = skipped.slice(0, SKIPPED_LIST_CAP);
	const rest = skipped.length - shown.length;
	const items = shown.map((s) => `<li>${escapeHtml(s)}</li>`).join('');
	const more = rest > 0 ? `<li>… and ${rest} more</li>` : '';

	return `<details class="race-import-skipped">`
		+ `<summary>${skipped.length} row(s) skipped — why</summary>`
		+ `<ul>${items}${more}</ul>`
		+ `</details>`;
}

/**
 * `getCheckpoints` is gone: the checkpoints the pickers are built from now travel on the preview, so a
 * checkpoint added on the profile is picked up by the next preview round-trip rather than by this module
 * remembering to re-read a getter.
 */
export function initRaceImportPanel(raceId: number, onImported: () => void): void {
	const panel = el('raceImport');
	if (!panel) return;

	const fileInput = el<HTMLInputElement>('raceCsvFile');
	const mapping = el('raceCsvMapping');
	const status = el('raceImportStatus');
	const splits = el('raceCsvSplits');
	const previewMount = el('raceImportPreview');
	const skippedMount = el('raceImportSkipped');
	const perLegToggle = el<HTMLInputElement>('csvPerLeg');
	const finishLabel = el('csvFinishLabel');

	let preview: RaceImportPreviewDto | null = null;
	/** Set once, by the user's own choice, and never re-guessed under them — see refresh(). */
	const chosen = new Map<string, string>();

	function say(message: string, isError = false, skipped: readonly string[] = []): void {
		if (status) {
			status.textContent = message;
			status.className = isError ? 'race-import-status is-error' : 'race-import-status';
		}
		// eslint-disable-next-line no-unsanitized/property -- renderSkippedList escapes every reason
		if (skippedMount) skippedMount.innerHTML = renderSkippedList(skipped);
	}

	/**
	 * A request is in flight: say what is happening, and keep saying it if the answer is slow.
	 *
	 * The status line used to read "Reading the file…" and then depend entirely on the request coming
	 * back. `asyncCallback` routes a rejection to console.error, so a 400, a 413 or a dropped connection
	 * left that sentence on screen FOREVER with the only evidence in devtools — indistinguishable from a
	 * hang. Every busy state now ends in a message, including the ones that end badly.
	 *
	 * Returns the finisher, which MUST be called (the `finally` blocks below do).
	 */
	function busy(message: string): () => void {
		if (status) {
			status.textContent = message;
			status.className = 'race-import-status is-busy';
		}
		// Server-side this work is ~20 ms even for the 1,387-rider file, so anything past a few seconds
		// is the network or the server, not the parse. Saying so beats a spinner that means nothing.
		const slow = window.setTimeout(() => {
			if (status) status.textContent = `${message} still waiting for the server…`;
		}, SLOW_REQUEST_MS);
		return () => window.clearTimeout(slow);
	}

	function file(): File | null {
		return fileInput?.files?.[0] ?? null;
	}

	/** The mapping as the pickers currently stand. Sent with every preview, import and profile save. */
	function currentMapping(): RaceCsvMappingBody {
		const value = (id: string): string | null => el<HTMLSelectElement>(id)?.value || null;
		const splitColumns: Record<number, string> = {};
		for (const select of splits?.querySelectorAll<HTMLSelectElement>('select[data-checkpoint-id]') ?? []) {
			if (select.value) splitColumns[Number(readData(select, 'checkpointId'))] = select.value;
		}
		return {
			nameColumn: value('csvName') ?? '',
			finishColumn: value('csvFinish'),
			bibColumn: value('csvBib'),
			categoryColumn: value('csvCategory'),
			genderColumn: value('csvGender'),
			clubColumn: value('csvClub'),
			statusColumn: value('csvStatus'),
			gunTimeColumn: value('csvGun'),
			netTimeColumn: value('csvNet'),
			startOffsetColumn: value('csvStartOffset'),
			waveColumn: value('csvWave'),
			disciplineColumn: value('csvDiscipline'),
			splitColumns,
			perLegTimes: perLegToggle?.checked === true,
		};
	}

	/** Re-render the pickers from the preview, keeping every choice the USER made. */
	function drawPickers(p: RaceImportPreviewDto): void {
		for (const [id, field] of FIELD_SELECTS) {
			const select = el<HTMLSelectElement>(id);
			if (!select) continue;
			const spec = p.fields.find((f) => f.field === field);
			// eslint-disable-next-line no-unsanitized/property -- renderColumnOptions escapeHtml()s every header
			select.innerHTML = renderColumnOptions(p.header, spec?.required === true);
			if (spec?.hint) select.title = spec.hint;
			// The user's choice wins over the guess, but only if the column still exists in this file.
			const picked = chosen.get(id);
			const survives = picked != null && p.header.includes(picked);
			select.value = survives ? picked : (spec?.guess ?? '');
		}

		if (splits) {
			// eslint-disable-next-line no-unsanitized/property -- renderSplitPickers escapes every value
			splits.innerHTML = renderSplitPickers(p.splitColumns, p.header);
			for (const hidden of splits.querySelectorAll<HTMLInputElement>('[data-guess]')) {
				const select = hidden.parentElement?.querySelector('select');
				if (select) select.value = readData(hidden, 'guess') ?? '';
			}
			for (const select of splits.querySelectorAll<HTMLSelectElement>('select[data-checkpoint-id]')) {
				const picked = chosen.get(select.id);
				if (picked != null && p.header.includes(picked)) select.value = picked;
			}
		}

		// Under per-section times the Finish picker stops meaning "his finish split" and starts meaning
		// "the number the file published" — which the section sum is checked AGAINST, never replaced by.
		if (finishLabel) {
			finishLabel.textContent = perLegToggle?.checked === true ? 'Published total (optional)' : 'Finish time';
		}
	}

	function drawPreview(p: RaceImportPreviewDto): void {
		if (previewMount) {
			// eslint-disable-next-line no-unsanitized/property -- both helpers escape every interpolation
			previewMount.innerHTML = renderWarnings(p.warnings) + renderPreviewRows(p);
		}
		say(
			`${p.rowCount} row(s), ${p.header.length} column(s) — read as ${delimiterLabel(p.delimiter)}-separated, `
			+ `${p.encoding}. Check the preview, then import.`,
			false,
			p.skipped);
	}

	/**
	 * Ask the server what it makes of the file under the current mapping.
	 *
	 * Every picker change round-trips, and that is deliberate: the numbers in the preview must be the
	 * numbers the import will produce, and the only way to guarantee that is to have the importer produce
	 * them. The file is small (a 1,400-rider CSV is ~200 KB) and the response is five rows.
	 */
	const refresh = asyncCallback('preview race csv', async () => {
		const chosenFile = file();
		if (!chosenFile) return;

		const done = busy(`Reading ${describeFile(chosenFile)}…`);
		try {
			const result = await previewRaceCsv(raceId, chosenFile, currentMapping());
			preview = result;
			drawPickers(result);
			drawPreview(result);
			if (mapping) mapping.hidden = false;
		} catch (error) {
			// The whole point of the catch: an error here used to reach console.error and nothing else,
			// leaving "Reading the file…" on screen with no way to tell a failure from a hang.
			say(`Could not read ${describeFile(chosenFile)}: ${(error as Error).message}`, true);
		} finally {
			done();
		}
	});

	/** Remember what the user picked, then re-preview under it. */
	function onPickerChanged(target: EventTarget | null): void {
		if (target instanceof HTMLSelectElement && target.id) {
			if (target.value) chosen.set(target.id, target.value);
			else chosen.delete(target.id);
		}
		if (preview) refresh();
	}

	/**
	 * Saved mappings. Applying one writes into `chosen` — the SAME store a manual pick writes to — so a
	 * profile and a click are indistinguishable from there on, and the next preview re-render keeps both.
	 */
	const profiles = createRaceImportProfiles(raceId, el<HTMLSelectElement>('csvProfile'), (profile) => {
		for (const [id, value] of profileSelections(profile, splitSelectId)) {
			if (value) chosen.set(id, value); else chosen.delete(id);
			const select = el<HTMLSelectElement>(id);
			if (select) select.value = value;
		}
		if (perLegToggle) perLegToggle.checked = profile.perLegTimes;
		if (preview) refresh();
	});

	el('csvProfileSave')?.addEventListener('click', asyncCallback('save race import mapping', async () => {
		const nameInput = el<HTMLInputElement>('csvProfileName');
		const name = nameInput?.value.trim();
		if (!name) { say('Name the mapping first.', true); return; }
		try {
			await profiles.save(name, currentMapping());
			if (nameInput) nameInput.value = '';
			say(`Saved the mapping as “${name}”. It will be offered on every race.`);
		} catch (error) {
			say(`Could not save “${name}”: ${(error as Error).message}`, true);
		}
	}));

	el('csvProfileDelete')?.addEventListener('click', asyncCallback('delete race import mapping', async () => {
		const chosenProfile = profiles.selected();
		if (!chosenProfile) { say('Pick a saved mapping to remove.', true); return; }
		try {
			await profiles.remove(chosenProfile.name);
			say(`Removed “${chosenProfile.name}”.`);
		} catch (error) {
			say(`Could not remove “${chosenProfile.name}”: ${(error as Error).message}`, true);
		}
	}));

	fileInput?.addEventListener('change', () => {
		chosen.clear();
		preview = null;
		// Nothing is echoed until the round trip lands, so a mis-picked file is otherwise invisible for
		// as long as the request takes. refresh() sets its own busy message immediately after this.
		const chosenFile = file();
		if (chosenFile) say(`Reading ${describeFile(chosenFile)}…`);
		refresh();
	});

	panel.addEventListener('change', (event) => {
		if (event.target === fileInput) return;
		// The profile picker has its own listener and must not be read as a column mapping.
		if (event.target instanceof HTMLSelectElement && event.target.id === 'csvProfile') return;
		onPickerChanged(event.target);
	});

	// The list is per USER, so it is worth having before a file is even chosen — that is the click a
	// returning user is here to make. A failure here is NOT worth a status message: it costs the user a
	// convenience, not the import, and the panel must not open with a red line about something they were
	// not doing. It stays a console-logged rejection on purpose.
	asyncCallback('list race import mappings', () => profiles.refresh())();

	el('raceImportForm')?.addEventListener('submit', asyncCallback('import race csv', async (event) => {
		event.preventDefault();
		const chosenFile = file();
		if (!chosenFile) { say('Choose a results file first.', true); return; }

		const map = currentMapping();
		if (map.perLegTimes) {
			const problem = perLegMappingError(map.finishColumn, map.splitColumns);
			if (problem) { say(problem, true); return; }
		}

		const done = busy(`Importing ${describeFile(chosenFile)}…`);
		try {
			const result = await importRaceCsv(raceId, chosenFile, map);
			say(
				importSummary(result.imported, result.finishers, result.skipped.length, result.overlappedLegCount),
				false,
				result.skipped);
			onImported();
		} catch (error) {
			// Through importRaceCsv → fail(), so `error.message` is the server's error STRING, not the
			// raw {"code":…,"message":…} envelope the raw text() used to surface.
			say(`Import failed: ${(error as Error).message}`, true);
		} finally {
			done();
		}
	}));
}

/**
 * What the import did — including the number the user must not be allowed to miss.
 *
 * `overlapped` are riders whose legs sum PAST their published finish, because a forced start ran two legs
 * concurrently and compressed the published clock. They are replayed on their LEG times, so they finish
 * later here than the published results say. That is deliberate, and stating it is the difference between
 * a considered decision and an apparent bug.
 */
export function importSummary(
	imported: number, finishers: number, skipped: number, overlapped: number,
): string {
	// An imported rider is NOT a finisher. This said "Imported 508 finishers" for a file in which 87 teams
	// never reached the line — and the replay knows they did not: it holds them at their last checkpoint
	// rather than walking them to a finish they never made. The server has always counted this correctly
	// (RaceCsvImportResult.FinisherCount); nothing read it.
	const parts = [`Imported ${imported} rider(s)`];
	if (finishers < imported) parts.push(`${finishers} finished, ${imported - finishers} did not`);
	if (skipped > 0) parts.push(`${skipped} row(s) skipped`);
	if (overlapped > 0) {
		parts.push(
			`${overlapped} team(s) whose legs run past their published finish — a forced start overlapped ` +
			`two legs, so they are replayed on their leg times (slower, and truer)`,
		);
	}
	return `${parts.join(' · ')}.`;
}
