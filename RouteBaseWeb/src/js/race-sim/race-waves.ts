/**
 * Start-block offsets — one number per WAVE, typed once.
 *
 * The offsets a file publishes are read at import: an explicit `start_offset` column, or gun − net when
 * both are mapped. This is the third case, and it is the common one — most files publish only a LABEL:
 * Schauinsland's blocks are called "Startblock 1: 10 Uhr bis 11 Uhr", which carries the start time in
 * prose and nothing a parser can use. Rather than guess at German-language time ranges, the label is
 * imported verbatim and the user assigns it a number.
 *
 * The editor appears only when the field actually HAS waves. A race imported without a wave column has
 * nothing to show here, and an empty panel is a question the user cannot answer.
 */

import { asyncCallback } from '../core/async-listener';
import { escapeHtml } from '../core/html';
import { setRaceWaveOffsets } from './race-api';
import type { RaceParticipantDto } from './race-api';

/** One start block: its label, how many entrants are in it, and the offset they currently carry. */
export interface RaceWave {
	readonly wave: string;
	readonly count: number;
	/** Null when the block has no offset yet, or when its riders disagree about theirs. */
	readonly offsetSeconds: number | null;
}

/**
 * The distinct waves in a field, in first-seen order.
 *
 * First-seen rather than alphabetical: results files list blocks in start order, and "Startblock 10"
 * sorting before "Startblock 2" would put the editor's rows in an order the user has to fight.
 *
 * A block whose riders carry DIFFERENT offsets reports null rather than one of them — that is a state
 * only a partial write can produce, and showing one rider's number as the block's would invite the user
 * to "confirm" a value that is not true of the others.
 */
export function wavesOf(participants: readonly RaceParticipantDto[]): RaceWave[] {
	const order: string[] = [];
	const counts = new Map<string, number>();
	const offsets = new Map<string, number | null>();
	const mixed = new Set<string>();

	for (const p of participants) {
		const wave = p.wave?.trim();
		if (!wave) continue;

		if (!counts.has(wave)) {
			order.push(wave);
			offsets.set(wave, p.startOffsetSeconds ?? null);
		} else if (offsets.get(wave) !== (p.startOffsetSeconds ?? null)) {
			mixed.add(wave);
		}
		counts.set(wave, (counts.get(wave) ?? 0) + 1);
	}

	return order.map((wave) => ({
		wave,
		count: counts.get(wave) ?? 0,
		offsetSeconds: mixed.has(wave) ? null : offsets.get(wave) ?? null,
	}));
}

/**
 * Seconds → `h:mm:ss` for the input, or blank. The same format the offset is TYPED in, so the field
 * round-trips: a value the editor renders must be one the parser below reads back unchanged.
 */
export function formatOffset(seconds: number | null): string {
	if (seconds == null || !Number.isFinite(seconds) || seconds < 0) return '';
	const whole = Math.round(seconds);
	const h = Math.floor(whole / 3600);
	const m = Math.floor((whole % 3600) / 60);
	const s = whole % 60;
	return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}

/**
 * `h:mm:ss`, `mm:ss` or bare seconds → seconds. Blank is null, which CLEARS the block back to a mass
 * start; anything unreadable is `undefined`, which is left alone rather than written as zero.
 */
export function parseOffset(text: string): number | null | undefined {
	const trimmed = text.trim();
	if (trimmed.length === 0) return null;

	const parts = trimmed.split(':');
	if (parts.length > 3) return undefined;

	let total = 0;
	for (const part of parts) {
		const n = Number(part);
		if (!Number.isFinite(n) || n < 0) return undefined;
		total = total * 60 + n;
	}
	return total;
}

/** The editor's rows. The label is the file's own text, so it is escaped. */
export function renderWaveRows(waves: readonly RaceWave[]): string {
	return waves.map((w) => `
		<label class="race-wave-row">
			<span class="race-wave-name">${escapeHtml(w.wave)}</span>
			<small>${w.count} rider(s)</small>
			<input type="text" class="race-select race-wave-offset" data-wave="${escapeHtml(w.wave)}"
			       value="${escapeHtml(formatOffset(w.offsetSeconds))}"
			       placeholder="0:00:00" aria-label="Start offset for ${escapeHtml(w.wave)}" />
		</label>`).join('');
}

/**
 * Swap an element for a fresh copy of itself, dropping every listener bound to the old node. Returns the
 * copy, or null when there was nothing to replace.
 */
function replaceWithClone(element: HTMLElement | null): HTMLElement | null {
	if (!element) return null;
	const clone = element.cloneNode(true) as HTMLElement;
	element.replaceWith(clone);
	return clone;
}

/**
 * Wire the wave editor. No-op when the field has no waves — the panel stays hidden and no listener is
 * attached, so a race that never imported a block column is unchanged in every respect.
 *
 * Safe to call again: the owner page re-runs it on every payload read, and the rows are re-rendered from
 * the participants it is given.
 */
export function initRaceWaveEditor(
	raceId: number, participants: readonly RaceParticipantDto[], onSaved: () => void,
): void {
	const panel = document.getElementById('raceWaves');
	const rows = document.getElementById('raceWaveRows');
	const status = document.getElementById('raceWaveStatus');
	if (!panel || !rows) return;

	const waves = wavesOf(participants);
	panel.hidden = waves.length === 0;
	if (waves.length === 0) return;

	// eslint-disable-next-line no-unsanitized/property -- renderWaveRows escapes every label
	rows.innerHTML = renderWaveRows(waves);

	// The rows are replaced above, but the SAVE button is outside them and survives — so a second call
	// would stack a second click listener on it and PUT the offsets twice. Replacing the node drops every
	// listener the previous call left, which is the only way to bind exactly once per render.
	const save = replaceWithClone(document.getElementById('raceWaveSave'));

	save?.addEventListener('click', asyncCallback('save race waves', async () => {
		const offsets: Record<string, number | null> = {};
		for (const input of rows.querySelectorAll<HTMLInputElement>('.race-wave-offset')) {
			const wave = input.dataset['wave'];
			if (wave == null) continue;
			const parsed = parseOffset(input.value);
			// `undefined` means unreadable: left out of the request entirely rather than sent as 0, which
			// would move a whole block to the gun on a typo.
			if (parsed !== undefined) offsets[wave] = parsed;
		}

		const result = await setRaceWaveOffsets(raceId, offsets);
		if (status) status.textContent = `Start times saved for ${result.updated} rider(s).`;
		onSaved();
	}));
}
