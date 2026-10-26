/**
 * add-track-dialog.ts — the "Add track to editor" dialog (A4 §3.4): a suggestion list (proximity ∪ same-day,
 * reason-flagged) that becomes a name/date search as the user types, and a single-pick that turns a row into
 * ONE `loadActivity` op via the controller's write-then-apply `addTrack` (D-A4-4 — the twin can't validate an
 * unseen source, so this one flow writes first, then applies the server's authoritative script). All user text
 * goes through `textContent` (names are safe by construction). Query-point derivation is a pure export (V21).
 */

import { setData } from '../core/data-attrs';
import type { SlotTrack } from './apply-script';
import type { SuggestedActivityDto, TrackSuggestionsResponse } from './edit-session-api';
import { EditApiError } from './edit-api';
import { trackEditErrors } from './track-edit-errors';
import { el, actionButton, buildDialog, type DialogHandle } from './track-edit-dom';
import { shiftedUtcDate } from './edit-gap-labels';

const DEFAULT_DEBOUNCE_MS = 300;

export interface AddTrackQueryPoint { endLat?: number; endLng?: number; localDate?: string }

/** Query point for suggestions from the CURRENT active-slot preview: last present lat/lng pair (nearest-present
 * from the tail, NaN-skip) + first present time → localDate in the slot's root tz (D-A4-7). Omits whatever's
 * unavailable (M5/M6). Pure/exported for tests (V21). */
export function buildQueryPoint(slot: SlotTrack, tzOffsetHours: number): AddTrackQueryPoint {
	const qp: AddTrackQueryPoint = {};
	const { lat, lng, time } = slot.channels;
	if (lat && lng) {
		for (let i = slot.count - 1; i >= 0; i--) {
			const la = lat[i] ?? NaN, ln = lng[i] ?? NaN;
			if (!Number.isNaN(la) && !Number.isNaN(ln)) { qp.endLat = la; qp.endLng = ln; break; }
		}
	}
	if (time) {
		for (let i = 0; i < slot.count; i++) {
			const t = time[i] ?? NaN;
			if (!Number.isNaN(t)) { qp.localDate = toLocalDate(t, tzOffsetHours); break; }
		}
	}
	return qp;
}

export interface AddTrackDialogDeps {
	fetchSuggestions(): Promise<TrackSuggestionsResponse>;
	search(q: string | undefined, date: string | undefined): Promise<TrackSuggestionsResponse>;
	/** Controller write-then-apply (D-A4-4); resolves on load, rejects with an EditApiError on a guard/engine miss. */
	addTrack(trackId: number): Promise<void>;
	/** Flow-B link href (A5's picker page); a plain link — degrades to the Activities page (M14). */
	pickerUrl?: string;
	debounceMs?: number;
}

export function openAddTrackDialog(deps: AddTrackDialogDeps): DialogHandle {
	const { backdrop, body: dialog, close } = buildDialog('Add track to editor', 'track-edit-add-dialog');

	const searchInput = document.createElement('input');
	searchInput.type = 'text';
	searchInput.placeholder = 'Search by name…';
	searchInput.setAttribute('aria-label', 'Search activities by name'); // placeholder is not an accessible name
	searchInput.className = 'track-edit-add-search';
	const dateInput = document.createElement('input');
	dateInput.type = 'date';
	dateInput.setAttribute('aria-label', 'Filter activities by date');
	dateInput.className = 'track-edit-add-date';
	const controls = el('div', 'track-edit-add-controls');
	controls.append(searchInput, dateInput);
	dialog.appendChild(controls);

	const list = el('div', 'track-edit-add-list');
	dialog.appendChild(list);

	const footer = el('div', 'track-edit-add-footer');
	if (deps.pickerUrl) {
		const link = document.createElement('a');
		link.href = deps.pickerUrl;
		link.textContent = 'Pick more at once on the Activities page ↗';
		link.className = 'track-edit-add-picker-link';
		footer.appendChild(link);
	}
	const cancelBtn = actionButton('Cancel', 'btn-cockpit-ghost');
	footer.appendChild(cancelBtn);
	dialog.appendChild(footer);

	cancelBtn.addEventListener('click', close);

	function renderRows(response: TrackSuggestionsResponse): void {
		list.replaceChildren();
		if (response.suggestions.length === 0) {
			const empty = el('div', 'track-edit-add-empty');
			empty.textContent = 'No matching activities.';
			list.appendChild(empty);
			return;
		}
		for (const dto of response.suggestions) list.appendChild(suggestionRow(dto));
	}

	function suggestionRow(dto: SuggestedActivityDto): HTMLElement {
		const row = document.createElement('button');
		row.type = 'button';
		row.className = 'track-edit-add-row';
		setData(row, 'trackId', String(dto.trackId));
		const name = el('span', 'track-edit-add-name');
		name.textContent = dto.name;
		const meta = el('span', 'track-edit-add-meta');
		meta.textContent = metaLine(dto);
		const reasons = el('span', 'track-edit-add-reasons');
		if (dto.alreadyLoaded) reasons.textContent = 'in editor';
		else reasons.textContent = reasonText(dto);
		row.append(name, meta, reasons);

		if (dto.alreadyLoaded) {
			row.disabled = true;
			row.classList.add('is-loaded');
		} else {
			row.addEventListener('click', () => void pick(dto, row, reasons));
		}
		return row;
	}

	async function pick(dto: SuggestedActivityDto, row: HTMLButtonElement, reasons: HTMLElement): Promise<void> {
		row.disabled = true;
		row.classList.add('is-busy');
		try {
			await deps.addTrack(dto.trackId);
			close();
		} catch (err) {
			row.classList.remove('is-busy');
			if (err instanceof EditApiError && err.code === trackEditErrors.duplicateLoad) {
				row.classList.add('is-loaded'); // M4 race: loaded elsewhere between render and click
				reasons.textContent = 'in editor';
			} else {
				row.disabled = false; // other errors: the controller toasts; let them retry
			}
		}
	}

	// Typing / date → debounced search; empty both → suggestions (D-A4-8).
	let debounce: ReturnType<typeof setTimeout> | undefined;
	function onQueryChange(): void {
		clearTimeout(debounce);
		debounce = setTimeout(() => {
			const q = searchInput.value.trim() || undefined;
			const date = dateInput.value || undefined;
			const load = q || date ? deps.search(q, date) : deps.fetchSuggestions();
			void load.then(renderRows).catch(() => renderRows({ suggestions: [] }));
		}, deps.debounceMs ?? DEFAULT_DEBOUNCE_MS);
	}
	searchInput.addEventListener('input', onQueryChange);
	dateInput.addEventListener('change', onQueryChange);

	void deps.fetchSuggestions().then(renderRows).catch(() => renderRows({ suggestions: [] }));
	return { el: backdrop, close };
}

// ---- pure formatting ----

function reasonText(dto: SuggestedActivityDto): string {
	const parts: string[] = [];
	if (dto.nearEnd && dto.startGapMeters != null) parts.push(`starts ${Math.round(dto.startGapMeters)} m from the end`);
	if (dto.sameDay) parts.push('same day');
	return parts.join(' · ');
}

function metaLine(dto: SuggestedActivityDto): string {
	const parts: string[] = [];
	if (dto.activityType) parts.push(dto.activityType);
	if (dto.startDateLocal) parts.push(dto.startDateLocal.slice(0, 16).replace('T', ' '));
	if (dto.totalDistanceMeters != null) parts.push(`${(dto.totalDistanceMeters / 1000).toFixed(1)} km`);
	return parts.join(' · ');
}

function toLocalDate(epochSeconds: number, tzOffsetHours: number): string {
	const shifted = shiftedUtcDate(epochSeconds, tzOffsetHours);
	const y = shifted.getUTCFullYear();
	const m = String(shifted.getUTCMonth() + 1).padStart(2, '0');
	const d = String(shifted.getUTCDate()).padStart(2, '0');
	return `${y}-${m}-${d}`;
}
