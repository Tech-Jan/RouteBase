/**
 * Activities grid — Select-mode staged bulk-edit bar.
 *
 * In "Select" mode the grid shows a checkbox column; this controller owns the bar that acts on the
 * checked set. Choosing a visibility / type / delete only STAGES the change (UI-side, per-row) — nothing
 * is written until **Done**, and **Cancel** discards everything. Staged rows repaint live via the grid's
 * redrawRows (the `type` column formatter reads getOverride), so the new value / greying shows before commit.
 *
 * Done commits in one pass: one POST /api/activities/bulk/update per distinct value-bucket, then one
 * POST /api/activities/bulk/delete for the staged deletions — each chunked under the server cap. Counts
 * come from the server (owner-scoping silently skips rows the user doesn't own), so shortfalls are surfaced.
 */

import { escapeHtml } from '../core/grid/grid-view';
import type { BulkUpdateActivitiesResponse, BulkDeleteActivitiesResponse, ApiError } from '../generated/contracts';

const VISIBILITIES = [
	{ value: 'private', label: 'Private' },
	{ value: 'friends', label: 'Friends' },
	{ value: 'public', label: 'Public' },
] as const;

/**
 * Reads the count field off a bulk response, LOUDLY. The `field` keys are tied to the generated
 * C# BulkUpdateActivitiesResponse / BulkDeleteActivitiesResponse (a rename breaks compile here), but the
 * body stays `unknown`: a wire-shape drift must throw at runtime — silently coercing a missing field to 0
 * turned every successful commit into a false "the rest aren't yours to change" alert.
 */
function readCount(json: unknown, field: keyof BulkUpdateActivitiesResponse | keyof BulkDeleteActivitiesResponse): number {
	const v = (json as Record<string, unknown>)[field];
	if (typeof v !== 'number') throw new Error(`Bulk response is missing "${field}" — wire shape drifted.`);
	return v;
}

/** A row's staged change: any of a new visibility, a new type, or a pending trash. */
export interface PendingEdit {
	visibility?: string;
	activityType?: string;
	deleted?: boolean;
}

/** Grid hooks the controller needs, injected once the GridView exists (see attach). */
export interface BulkDeps {
	/** Repaint the on-screen rows in place (GridView.redrawRows) so staged values show. */
	redraw: () => void;
	/** Leave Select mode (reload out of ?select=1). */
	exit: () => void;
	/** Ids currently passing the active filters (the grid's last filteredIds — every page of the
	 *  filtered set, not just the visible one). Read at commit time for the hidden-rows warning. */
	visibleIds: () => ReadonlySet<number>;
}

export interface BulkController {
	mountBar(toolbar: HTMLElement): void;
	/** Replace the current selection — called from the grid's onSelectionChange. */
	setSelection(ids: Set<number>): void;
	/** The staged change for a row, for the column formatter to overlay. */
	getOverride(trackId: number): PendingEdit | undefined;
	/** Wire the grid repaint + mode-exit hooks (createBulkController runs before the grid exists). */
	attach(deps: BulkDeps): void;
}

// Mirrors the server's MaxTrackIds (ActivitiesApiController). The cap bounds a single request's IN (...)
// against the DB; larger selections are split into sequential batches here so "select all filtered" works
// at any size — the user never has to narrow the filter or page through to reach the rest.
export const MAX_BULK_IDS = 10000;

/** Split ids into batches of at most `size`. Pure — no DOM — so it is unit-testable. */
export function chunkIds(ids: number[], size = MAX_BULK_IDS): number[][] {
	if (ids.length === 0) return [];
	if (ids.length <= size) return [ids];
	const out: number[][] = [];
	for (let i = 0; i < ids.length; i += size) out.push(ids.slice(i, i + size));
	return out;
}

/**
 * Groups staged edits into the minimal set of bulk/update payloads — one per distinct (visibility, type)
 * combo. Rows staged for deletion are excluded (they'll be trashed, not updated). Pure — unit-testable.
 */
export function bucketEdits(
	pending: Map<number, PendingEdit>,
): Array<{ trackIds: number[]; visibility?: string | undefined; activityType?: string | undefined }> {
	const buckets = new Map<string, { trackIds: number[]; visibility?: string | undefined; activityType?: string | undefined }>();
	for (const [id, edit] of pending) {
		if (edit.deleted) continue; // delete wins over any staged field change
		if (edit.visibility === undefined && edit.activityType === undefined) continue;
		// '|' separator is unambiguous: visibility is a closed vocabulary (private/friends/public or
		// empty), so the first '|' always ends it, whatever characters the activity type contains.
		// (An earlier version used a raw NUL byte here, which made git treat this whole file as binary
		// and blinded every diff/review/grep of it — never put a control byte in source.)
		const key = `${edit.visibility ?? ''}|${edit.activityType ?? ''}`;
		let b = buckets.get(key);
		if (!b) {
			b = { trackIds: [], visibility: edit.visibility, activityType: edit.activityType };
			buckets.set(key, b);
		}
		b.trackIds.push(id);
	}
	return [...buckets.values()];
}

/** Track ids staged for deletion. Pure — unit-testable. */
export function collectDeletes(pending: Map<number, PendingEdit>): number[] {
	const ids: number[] = [];
	for (const [id, edit] of pending) if (edit.deleted) ids.push(id);
	return ids;
}

/**
 * FE-07: the selection deliberately SURVIVES filter narrowing (user decision — never pruned), so a
 * commit can write rows the user no longer sees. Returns the confirm-dialog warning line when any
 * acted-on row is hidden by the active filters, null when all are visible. Pure — unit-testable.
 */
export function hiddenRowsWarning(actedIds: number[], visibleIds: ReadonlySet<number>): string | null {
	const hidden = actedIds.filter((id) => !visibleIds.has(id)).length;
	if (hidden === 0) return null;
	return `${actedIds.length} selected, ${hidden} currently hidden by filters — proceed?`;
}

/**
 * Message shown when the server changed fewer rows than were staged (owner-scoping skipped the rest).
 * Pure — no DOM — so it is unit-testable. Returns null when everything staged was applied.
 */
export function skipMessage(applied: number, staged: number, verb: string): string | null {
	if (applied >= staged) return null;
	return `${verb} ${applied} of ${staged} — the rest aren't yours to change.`;
}

async function errorText(res: Response): Promise<string> {
	try {
		const body: Partial<ApiError> = await res.json();
		if (body.message) return body.message;
	} catch {
		/* non-JSON body — fall through to the status text */
	}
	return `Request failed (${res.status}).`;
}

export function createBulkController(activityTypes: string[]): BulkController {
	let selected: number[] = [];
	let deps: BulkDeps | null = null;
	let committing = false;
	// Per-row staged changes, keyed by trackId. Survives paging/sorting (the formatter re-reads it).
	const pending = new Map<number, PendingEdit>();

	let bar: HTMLElement | null = null;
	let countEl: HTMLElement | null = null;
	let visSel: HTMLSelectElement | null = null;
	let typeSel: HTMLSelectElement | null = null;
	let delBtn: HTMLButtonElement | null = null;
	let doneBtn: HTMLButtonElement | null = null;
	let doneBadge: HTMLSpanElement | null = null;
	let cancelBtn: HTMLButtonElement | null = null;

	/** Number of rows carrying any staged change — drives the Done button + its badge. */
	function dirtyCount(): number {
		let c = 0;
		for (const e of pending.values())
			if (e.deleted || e.visibility !== undefined || e.activityType !== undefined) c++;
		return c;
	}

	function refresh(): void {
		if (!bar) return;
		const n = selected.length;
		if (countEl) countEl.textContent = n === 1 ? '1 selected' : `${n} selected`;
		// The value/delete controls need a selection; Done needs staged changes; all lock during a commit.
		if (visSel) visSel.disabled = n === 0 || committing;
		if (typeSel) typeSel.disabled = n === 0 || committing;
		if (delBtn) delBtn.disabled = n === 0 || committing;
		const dirty = dirtyCount();
		if (doneBtn) {
			doneBtn.disabled = dirty === 0 || committing;
			doneBtn.classList.toggle('act-bulk-done--dirty', dirty > 0);
		}
		if (doneBadge) {
			doneBadge.textContent = String(dirty);
			doneBadge.hidden = dirty === 0;
		}
		if (cancelBtn) cancelBtn.disabled = committing;
	}

	function stageUpdate(patch: { visibility?: string; activityType?: string }): void {
		if (committing || selected.length === 0) return;
		for (const id of selected) {
			const e = pending.get(id) ?? {};
			if (patch.visibility !== undefined) e.visibility = patch.visibility;
			if (patch.activityType !== undefined) e.activityType = patch.activityType;
			pending.set(id, e);
		}
		deps?.redraw();
		refresh();
	}

	function stageDelete(): void {
		if (committing || selected.length === 0) return;
		for (const id of selected) {
			const e = pending.get(id) ?? {};
			e.deleted = true;
			pending.set(id, e);
		}
		deps?.redraw();
		refresh();
	}

	async function post(path: string, body: unknown): Promise<Response> {
		return fetch(path, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(body),
		});
	}

	async function commit(): Promise<void> {
		if (committing) return;
		const buckets = bucketEdits(pending);
		const deletes = collectDeletes(pending);
		const editCount = buckets.reduce((s, b) => s + b.trackIds.length, 0);
		if (editCount === 0 && deletes.length === 0) {
			deps?.exit(); // nothing staged → just leave Select mode
			return;
		}

		const parts: string[] = [];
		if (editCount > 0) parts.push(`${editCount} change${editCount === 1 ? '' : 's'}`);
		if (deletes.length > 0) parts.push(`${deletes.length} deletion${deletes.length === 1 ? '' : 's'}`);
		// FE-07 hidden-rows guard: the selection survives filter narrowing, so warn when the commit
		// would write rows the active filters currently hide.
		const actedIds = [...new Set([...buckets.flatMap((b) => b.trackIds), ...deletes])];
		const warning = deps ? hiddenRowsWarning(actedIds, deps.visibleIds()) : null;
		const message =
			`Apply ${parts.join(' and ')}?` +
			(warning ? `\n\n${warning}` : '') +
			'\n\nDeletions move activities to the trash (restorable).';
		if (!confirm(message)) return;

		committing = true;
		refresh();
		try {
			let editApplied = 0;
			for (const b of buckets) {
				const patch: { visibility?: string; activityType?: string } = {};
				if (b.visibility !== undefined) patch.visibility = b.visibility;
				if (b.activityType !== undefined) patch.activityType = b.activityType;
				for (const batch of chunkIds(b.trackIds)) {
					const res = await post('/api/activities/bulk/update', { trackIds: batch, ...patch });
					if (!res.ok) {
						alert(await errorText(res));
						return; // leave staged state intact so the user can retry
					}
					editApplied += readCount(await res.json(), 'updated');
				}
			}
			let delApplied = 0;
			for (const batch of chunkIds(deletes)) {
				const res = await post('/api/activities/bulk/delete', { trackIds: batch });
				if (!res.ok) {
					alert(await errorText(res));
					return;
				}
				delApplied += readCount(await res.json(), 'deleted');
			}
			const msgs = [
				skipMessage(editApplied, editCount, 'Changed'),
				skipMessage(delApplied, deletes.length, 'Moved'),
			].filter((m): m is string => m !== null);
			if (msgs.length) alert(msgs.join('\n'));
			deps?.exit(); // reload out of Select mode showing the committed state
		} catch (err) {
			// Writes up to this point are real (each batch committed server-side) — reload out of Select
			// mode so the grid shows the truth rather than staged state that half-happened.
			alert(err instanceof Error ? err.message : 'Bulk commit failed unexpectedly.');
			deps?.exit();
		} finally {
			committing = false;
			refresh();
		}
	}

	function cancel(): void {
		if (committing) return;
		if (dirtyCount() > 0 && !confirm('Discard staged changes?')) return;
		pending.clear();
		deps?.exit(); // nothing was written, so reloading out of the mode restores the original data
	}

	return {
		mountBar(toolbar: HTMLElement): void {
			bar = document.createElement('div');
			bar.className = 'act-bulk-bar';

			countEl = document.createElement('span');
			countEl.className = 'act-bulk-count';
			countEl.textContent = '0 selected';

			visSel = document.createElement('select');
			visSel.className = 'grid-toolbar-btn act-bulk-select';
			visSel.title = 'Stage a visibility for the selected activities';
			// eslint-disable-next-line no-unsanitized/property -- VISIBILITIES is a module constant, no wire data
			visSel.innerHTML =
				'<option value="" disabled selected>Visibility…</option>' +
				VISIBILITIES.map((v) => `<option value="${v.value}">${v.label}</option>`).join('');
			const visSelEl = visSel; // narrowed rebind for the change closure (G3)
			visSelEl.addEventListener('change', () => {
				const value = visSelEl.value;
				visSelEl.selectedIndex = 0; // reset to placeholder so the same choice can be re-applied
				if (value) stageUpdate({ visibility: value });
			});

			typeSel = document.createElement('select');
			typeSel.className = 'grid-toolbar-btn act-bulk-select';
			typeSel.title = 'Stage an activity type for the selected activities';
			// eslint-disable-next-line no-unsanitized/property -- each activity type is escapeHtml()'d
			typeSel.innerHTML =
				'<option value="" disabled selected>Type…</option>' +
				activityTypes
					.filter((t) => t)
					.map((t) => `<option value="${escapeHtml(t)}">${escapeHtml(t)}</option>`)
					.join('');
			const typeSelEl = typeSel; // narrowed rebind for the change closure (G3)
			typeSelEl.addEventListener('change', () => {
				const value = typeSelEl.value;
				typeSelEl.selectedIndex = 0;
				if (value) stageUpdate({ activityType: value });
			});

			delBtn = document.createElement('button');
			delBtn.type = 'button';
			delBtn.className = 'grid-toolbar-btn act-bulk-delete';
			delBtn.title = 'Stage the selected activities for deletion (moved to trash on Done)';
			delBtn.innerHTML = '<i class="bi bi-trash"></i> Delete';
			delBtn.addEventListener('click', () => stageDelete());

			doneBtn = document.createElement('button');
			doneBtn.type = 'button';
			doneBtn.className = 'grid-toolbar-btn act-bulk-done';
			doneBtn.title = 'Write the staged changes';
			doneBadge = document.createElement('span');
			doneBadge.className = 'act-bulk-badge';
			doneBadge.hidden = true;
			doneBtn.innerHTML = '<i class="bi bi-check2"></i> Done ';
			doneBtn.appendChild(doneBadge);
			doneBtn.addEventListener('click', () => void commit());

			cancelBtn = document.createElement('button');
			cancelBtn.type = 'button';
			cancelBtn.className = 'grid-toolbar-btn act-bulk-cancel';
			cancelBtn.title = 'Discard staged changes and leave Select mode';
			cancelBtn.innerHTML = '<i class="bi bi-x-lg"></i> Cancel';
			cancelBtn.addEventListener('click', () => cancel());

			bar.append(countEl, visSel, typeSel, delBtn, doneBtn, cancelBtn);
			toolbar.appendChild(bar);
			refresh();
		},
		setSelection(ids: Set<number>): void {
			selected = [...ids];
			refresh();
		},
		getOverride(trackId: number): PendingEdit | undefined {
			return pending.get(trackId);
		},
		attach(d: BulkDeps): void {
			deps = d;
		},
	};
}
