/**
 * The course beside the profile, edited as SECTIONS — not as checkpoints.
 *
 * A relay or stage race is a list of sections ("xcski, dhski, run, roadbike, canoe, cyclocross, kayak");
 * the checkpoints are merely where they meet. Each row reads left to right — "xcski, blue, ends at 5.103 km".
 * A section's name and colour live on the checkpoint it FOLLOWS, and its end IS the next checkpoint; naming
 * a section therefore also names the checkpoint that ends it (one concept, one door). The Finish keeps its
 * own name — it ends the course, not just a section.
 *
 * ## Staged editing (draft → Apply / Cancel)
 *
 * Edits do NOT hit the server as you make them. They accumulate in a DRAFT that previews live on the
 * profile (via onPreview), and nothing is persisted — and the 424-rider field is not re-paced — until
 * **Apply**. **Cancel** throws the draft away and snaps back to the last applied state. This keeps a
 * half-finished set of section boundaries from re-pacing the field on every keystroke, and makes a course
 * edit reversible.
 *
 * ## Placing a checkpoint
 *
 * Placement is armed by the **Add checkpoint** button and only then: the next click on the SPEED CHART
 * splits a section. Otherwise a click there selects or drags an existing boundary — it never places a new
 * one. (Why a chart and not the map: race-profile.ts — the course repeats, so a map point is an ambiguous
 * chainage, while both charts' x-axes are chainage. The fullscreen elevation strip still places too; the
 * speed chart is simply the one that is on screen.)
 *
 * Owner-only. Every write goes through the API on Apply and the server is the authority: a rejected edit
 * (a line dragged off the course, a colour that is not #rrggbb) is shown, not swallowed.
 *
 * ## Start and Finish are placed, not implied
 *
 * The course grid is the whole recorded ride and the race is a WINDOW on it, so both lines are typed here
 * like any other boundary. They stay the extremes — the server bounds them (RaceCheckpointBounds) — and the
 * readout under the list keeps "race distance" and "ride distance" visibly apart, which is the confusion
 * that seeded a Finish 337 m short of a 70 km course in the first place.
 */

import { readData } from '../core/data-attrs';
import { must } from '../core/invariant';
import { escapeHtml } from '../core/html';
import {
	addCheckpoint,
	deleteCheckpoint,
	updateCheckpoint,
	type RaceCheckpointEdit,
} from './race-api';
import type { RaceCheckpointDto } from './race-api';

export interface RaceCheckpointEditorOptions {
	readonly raceId: number;
	readonly mount: HTMLElement;
	readonly editable: boolean;
	/**
	 * The course GRID's length — the ride, not the race. Bounds the two endpoints, and is the second half
	 * of the distance readout: "race 69.730 km · ride 70.930 km" are different numbers the moment a line is
	 * moved off an end, and conflating them is what put a Finish 337 m inside a course.
	 */
	readonly courseTotalMeters: number;
	/** Committed on Apply: the persisted checkpoints — redraw the profile AND re-pace the field. */
	readonly onChanged: (checkpoints: RaceCheckpointDto[]) => void;
	/** Every draft edit: redraw the profile + course line to preview it, but do NOT re-pace the field. */
	readonly onPreview?: ((checkpoints: RaceCheckpointDto[]) => void) | undefined;
	/** Placement mode armed/disarmed — forwarded to the profile so a click only places while armed. */
	readonly onArmPlacement?: ((armed: boolean) => void) | undefined;
	/** A section was focused — highlight the checkpoint that ENDS it on the profile. */
	readonly onSelect?: ((checkpointId: number | null) => void) | undefined;
}

export interface RaceCheckpointEditor {
	setCheckpoints(checkpoints: readonly RaceCheckpointDto[]): void;
	/** Split a section at a distance the profile was clicked at — a no-op unless placement is armed. */
	place(chainageMeters: number): void;
	/** Stage a drag of a section boundary. */
	move(checkpointId: number, chainageMeters: number): void;
	select(checkpointId: number | null): void;
	destroy(): void;
}

const DEFAULT_LEG_COLOR = '#4dabf7';

/** A section: the span between two consecutive checkpoints. Its name and colour live on `start`. */
export interface RaceSection {
	readonly index: number;
	readonly start: RaceCheckpointDto;
	readonly end: RaceCheckpointDto;
	/** The last section ends at the Finish, which may be moved but never removed. */
	readonly endsAtFinish: boolean;
}

/**
 * Checkpoints → the sections between them. Pure. N checkpoints make N-1 sections; a course with only Start
 * and Finish is one section.
 */
export function sectionsOf(checkpoints: readonly RaceCheckpointDto[]): RaceSection[] {
	const ordered = [...checkpoints].sort((a, b) => a.chainageMeters - b.chainageMeters);
	const sections: RaceSection[] = [];
	for (let i = 0; i < ordered.length - 1; i++) {
		sections.push({
			index: i,
			start: must(ordered[i], 'i < length-1 (loop-bounded)'),
			end: must(ordered[i + 1], 'i+1 <= length-1 (loop-bounded)'),
			endsAtFinish: i === ordered.length - 2,
		});
	}
	return sections;
}

/** A stable fingerprint of the fields Apply would persist — draft vs applied dirtiness turns on this. */
function signature(checkpoints: readonly RaceCheckpointDto[]): string {
	return [...checkpoints]
		.sort((a, b) => a.chainageMeters - b.chainageMeters)
		.map((c) => `${c.checkpointId}:${c.chainageMeters}:${c.name ?? ''}:${c.legName ?? ''}:${c.legColor ?? ''}`)
		.join('|');
}

export function initRaceCheckpointEditor(options: RaceCheckpointEditorOptions): RaceCheckpointEditor {
	const { raceId, mount, editable } = options;

	// applied = the last persisted truth; draft = the working copy every edit mutates. Apply reconciles the
	// two through the API; Cancel throws draft away and clones applied back.
	let applied: RaceCheckpointDto[] = [];
	let draft: RaceCheckpointDto[] = [];
	let selected: number | null = null;
	let placing = false;
	let busy = false;
	// New (not-yet-saved) checkpoints carry a negative id until Apply swaps in the server's real one.
	let tempSeq = -1;

	const toolbar = document.createElement('div');
	toolbar.className = 'race-cp-toolbar';
	const addBtn = document.createElement('button');
	addBtn.type = 'button';
	addBtn.className = 'btn-cockpit btn-cockpit-ghost btn-cockpit-xs race-cp-add';
	addBtn.innerHTML = '<i class="bi bi-plus-lg"></i> Add checkpoint';
	const spacer = document.createElement('span');
	spacer.className = 'race-cp-toolbar-spacer';
	const dirtyTag = document.createElement('span');
	dirtyTag.className = 'race-cp-dirty';
	dirtyTag.textContent = 'Unsaved';
	const cancelBtn = document.createElement('button');
	cancelBtn.type = 'button';
	cancelBtn.className = 'btn-cockpit btn-cockpit-ghost btn-cockpit-xs race-cp-cancel';
	cancelBtn.textContent = 'Cancel';
	const applyBtn = document.createElement('button');
	applyBtn.type = 'button';
	applyBtn.className = 'btn-cockpit btn-cockpit-primary btn-cockpit-xs race-cp-apply';
	applyBtn.textContent = 'Apply';
	toolbar.append(addBtn, spacer, dirtyTag, cancelBtn, applyBtn);

	const list = document.createElement('div');
	list.className = 'race-cp-list';
	// Two numbers that are NOT the same once a line sits off an end of the ride. Stating both is the cheap
	// half of the fix that made the endpoints movable in the first place.
	const distanceLine = document.createElement('p');
	distanceLine.className = 'race-cp-distance';
	const status = document.createElement('p');
	status.className = 'race-cp-status';

	if (editable) mount.append(toolbar, list, distanceLine, status);
	else mount.append(list, distanceLine, status);

	function say(message: string, isError = false): void {
		status.textContent = message;
		status.classList.toggle('is-error', isError);
	}

	function preview(): void {
		options.onPreview?.([...draft]);
	}

	function reseq(list: RaceCheckpointDto[]): void {
		list.sort((a, b) => a.chainageMeters - b.chainageMeters);
		list.forEach((c, i) => { (c as { seq: number }).seq = i; });
	}

	function isDirty(): boolean {
		return signature(draft) !== signature(applied);
	}

	/** Reflect draft state on the toolbar without rebuilding the rows (keeps a focused input alive). */
	function paintToolbar(): void {
		if (!editable) return;
		const dirty = isDirty();
		dirtyTag.classList.toggle('is-visible', dirty);
		applyBtn.disabled = !dirty || busy;
		cancelBtn.disabled = !dirty || busy;
		addBtn.classList.toggle('is-armed', placing);
		addBtn.disabled = busy;
	}

	/** The race is a window on the ride, so its length is the span between the lines — not the ride's. */
	function paintDistance(): void {
		const chainages = draft.map((c) => c.chainageMeters);
		if (chainages.length < 2) { distanceLine.textContent = ''; return; }
		const race = (Math.max(...chainages) - Math.min(...chainages)) / 1000;
		distanceLine.textContent =
			`Race ${race.toFixed(3)} km · ride ${(options.courseTotalMeters / 1000).toFixed(3)} km`;
	}

	function render(): void {
		const sections = sectionsOf(draft);
		paintDistance();
		if (sections.length === 0) {
			list.replaceChildren();
			paintToolbar();
			return;
		}

		const start = must(sections[0], 'non-empty sections (guarded above)').start;
		const startKm = (start.chainageMeters / 1000).toFixed(3);
		// The Start is a checkpoint like any other now: the race may begin after the roll-out rather than at
		// the ride's first GPS point.
		const head = `<div class="race-cp-endpoint" data-endpoint-id="${start.checkpointId}">
			<span class="race-cp-endpoint-label">Start</span>
			${editable
				? `<label class="race-cp-km">
						<input type="number" step="0.001" min="0" data-field="startKm" value="${startKm}">
						<span>km</span>
					</label>`
				: `<span class="race-cp-endpoint-km">${startKm} km</span>`}
		</div>`;

		const rows = sections
			.map((s) => {
				const disabled = editable ? '' : ' disabled';
				const km = (s.end.chainageMeters / 1000).toFixed(3);
				const color = s.start.legColor ?? DEFAULT_LEG_COLOR;

				const kmField = !editable
					? `<span class="race-cp-km-fixed">${km} km</span>`
					: `<label class="race-cp-km">
							<input type="number" step="0.001" min="0" data-field="endKm" value="${km}">
							<span>km</span>
						</label>`;

				const tail = s.endsAtFinish
					? '<span class="race-cp-finish-tag">Finish</span>'
					: editable
						? '<button type="button" class="btn-cockpit btn-cockpit-danger btn-cockpit-xs" data-action="delete" title="Merge this section into the next one"><i class="bi bi-x-lg"></i></button>'
						: '';

				// data-checkpoint-id is the END: it is what the profile draws, drags and highlights.
				return `
					<div class="race-cp-row${s.end.checkpointId === selected ? ' is-selected' : ''}"
					     data-checkpoint-id="${s.end.checkpointId}" data-leg-id="${s.start.checkpointId}">
						<span class="race-cp-index">${s.index + 1}</span>
						<input class="race-cp-name" data-field="sectionName" value="${escapeHtml(s.start.legName ?? '')}"
						       placeholder="section name"${disabled}>
						<input class="race-cp-color" type="color" data-field="sectionColor" value="${escapeHtml(color)}"${disabled}>
						<span class="race-cp-endsat">ends at</span>
						${kmField}
						${tail}
					</div>`;
			})
			.join('');

		// eslint-disable-next-line no-unsanitized/property -- every interpolated value is escapeHtml'd or numeric
		list.innerHTML = head + rows;
		paintToolbar();
	}

	/** Selection is a class, never a re-render — render() replaces innerHTML and would kill a live input. */
	function paintSelection(): void {
		for (const row of list.querySelectorAll<HTMLElement>('[data-checkpoint-id]')) {
			row.classList.toggle('is-selected', Number(readData(row, 'checkpointId')) === selected);
		}
	}

	function draftSectionOf(row: HTMLElement): RaceSection | undefined {
		const endId = Number(readData(row, 'checkpointId'));
		return sectionsOf(draft).find((s) => s.end.checkpointId === endId);
	}

	function setPlacing(next: boolean): void {
		placing = next;
		options.onArmPlacement?.(next);
		paintToolbar();
		say(next ? 'Click the speed chart to place a checkpoint.' : '');
	}

	// ── draft edits (staged; no API until Apply) ────────────────────────────────────────────────────────

	/** Metres from a km input, or null with the error already said. */
	function metresFrom(input: HTMLInputElement, minMetres: number): number | null {
		const raw = input.value.trim();
		const metres = raw === '' ? NaN : Math.round(Number(raw) * 1000);
		if (!Number.isFinite(metres) || metres < minMetres) { say('That is not a distance.', true); return null; }
		return metres;
	}

	list.addEventListener('change', (event) => {
		if (!editable) return;
		const input = (event.target as HTMLElement).closest<HTMLInputElement>('[data-field]');
		if (!input) return;
		const field = readData(input, 'field');

		// The Start sits outside the section rows — it ends nothing — so it is resolved from the draft's
		// lowest chainage rather than through draftSectionOf. 0 is legal: that is the ride's first point.
		if (field === 'startKm') {
			const metres = metresFrom(input, 0);
			if (metres == null) return;
			const target = [...draft].sort((a, b) => a.chainageMeters - b.chainageMeters)[0];
			if (!target) return;
			(target as { chainageMeters: number }).chainageMeters = metres;
			reseq(draft);
			render();
			preview();
			return;
		}

		const row = (event.target as HTMLElement).closest<HTMLElement>('[data-checkpoint-id]');
		if (!row) return;
		const section = draftSectionOf(row);
		if (!section) return;

		if (field === 'endKm') {
			// > 0: a section that ends at the start line has no length. Only the Start itself may be 0.
			const metres = metresFrom(input, 1);
			if (metres == null) return;
			(section.end as { chainageMeters: number }).chainageMeters = metres;
			reseq(draft);
			render();               // a boundary move can reorder the rows; the input already committed
			preview();
		} else if (field === 'sectionColor') {
			(section.start as { legColor: string | null }).legColor = input.value;
			preview();              // no reorder — keep the colour input alive, just repaint the profile
			paintToolbar();
		} else if (field === 'sectionName') {
			// Naming the section names the split that ends it too (one concept). The Finish keeps its own name.
			(section.start as { legName: string | null }).legName = input.value;
			if (!section.endsAtFinish) (section.end as { name: string | null }).name = input.value;
			preview();
			paintToolbar();
		}
	});

	list.addEventListener('click', (event) => {
		const target = event.target as HTMLElement;
		const row = target.closest<HTMLElement>('[data-checkpoint-id]');
		if (!row) return;
		const endId = Number(readData(row, 'checkpointId'));

		if (editable && target.closest('[data-action="delete"]')) {
			draft = draft.filter((c) => c.checkpointId !== endId);
			if (selected === endId) selected = null;
			reseq(draft);
			render();
			preview();
			return;
		}

		selected = endId;
		options.onSelect?.(endId);
		paintSelection();
	});

	// ── toolbar ─────────────────────────────────────────────────────────────────────────────────────────

	addBtn.addEventListener('click', () => { if (!busy) setPlacing(!placing); });
	cancelBtn.addEventListener('click', () => {
		if (busy) return;
		draft = applied.map((c) => ({ ...c }));
		selected = null;
		if (placing) setPlacing(false);
		render();
		preview();
		say('Reverted.');
	});
	applyBtn.addEventListener('click', () => { void apply(); });

	/** Reconcile draft → server: delete removed boundaries, create new ones, update changed fields. */
	async function apply(): Promise<void> {
		if (busy || !isDirty()) return;
		busy = true;
		paintToolbar();
		try {
			const draftIds = new Set(draft.map((c) => c.checkpointId));
			// Deletes: an applied boundary the draft no longer has.
			for (const c of applied) {
				if (c.checkpointId > 0 && !draftIds.has(c.checkpointId)) await deleteCheckpoint(raceId, c.checkpointId);
			}
			// Adds: a draft checkpoint with a temporary (negative) id. Create it, then apply any leg name/colour.
			for (const add of draft.filter((c) => c.checkpointId < 0)) {
				const created = await addCheckpoint(raceId, add.name ?? '', Math.round(add.chainageMeters));
				// Build the optional leg patch from the STAGED values first — `created` from a fresh add
				// carries null leg fields, so adopting it wholesale would erase what the user typed.
				const extra: RaceCheckpointEdit = {};
				if (add.legName != null && add.legName !== '') extra.legName = add.legName;
				if (add.legColor != null) extra.legColor = add.legColor;
				if (Object.keys(extra).length > 0) {
					// Adopt the real id the instant the create succeeds (PF-05): if this leg update throws, a
					// re-Apply must NOT create a SECOND checkpoint — the boundary already exists on the server.
					// The id is what turns a re-Apply into an update/no-op, never another add.
					Object.assign(add, { checkpointId: created.checkpointId, node: 0 });
					const finalRow = await updateCheckpoint(raceId, created.checkpointId, extra);
					Object.assign(add, { ...finalRow, node: 0 });
				} else {
					Object.assign(add, { ...created, node: 0 });
				}
			}
			// Updates: an existing checkpoint whose persisted fields changed.
			for (const d of draft.filter((c) => c.checkpointId > 0)) {
				const before = applied.find((c) => c.checkpointId === d.checkpointId);
				if (!before) continue;
				const edit: RaceCheckpointEdit = {};
				if (d.chainageMeters !== before.chainageMeters) edit.chainageMeters = Math.round(d.chainageMeters);
				// Edits only ever set these to a string (an input value, '' when cleared); a null is original
				// data, never a change — so gating on non-null both matches intent and satisfies the DTO type.
				if (d.name != null && d.name !== (before.name ?? '')) edit.name = d.name;
				if (d.legName != null && d.legName !== (before.legName ?? '')) edit.legName = d.legName;
				if (d.legColor != null && d.legColor !== (before.legColor ?? '')) edit.legColor = d.legColor;
				if (Object.keys(edit).length > 0) await updateCheckpoint(raceId, d.checkpointId, edit);
			}

			applied = draft.map((c) => ({ ...c }));
			reseq(applied);
			reseq(draft);
			busy = false;
			render();
			// Commit: the field re-paces here, and here only.
			options.onChanged([...applied]);
			say('Applied.');
		} catch (error) {
			busy = false;
			// The server's message is the useful one ("Start and Finish cannot be moved or removed"). The draft
			// is kept so the user can fix it and re-apply, or Cancel; the profile still shows the draft.
			say((error as Error).message, true);
			preview();
			paintToolbar();
		}
	}

	return {
		setCheckpoints(next) {
			applied = next.map((c) => ({ ...c }));
			draft = next.map((c) => ({ ...c }));
			if (placing) setPlacing(false);
			render();
		},
		place(chainageMeters) {
			// Gated: a profile click only places while the Add checkpoint button has armed it.
			if (!placing) return;
			// Splitting leaves the new section unnamed — the user names it in the row that just appeared.
			draft.push({
				checkpointId: tempSeq--, seq: 0, name: '', chainageMeters: Math.round(chainageMeters),
				legName: null, legColor: null, node: 0,
			});
			selected = null;
			reseq(draft);
			setPlacing(false);      // single placement per arming
			render();
			preview();
			say('Checkpoint added — Apply to save.');
		},
		move(checkpointId, chainageMeters) {
			const target = draft.find((c) => c.checkpointId === checkpointId);
			if (!target) return;
			(target as { chainageMeters: number }).chainageMeters = Math.round(chainageMeters);
			reseq(draft);
			render();
			preview();
		},
		select(checkpointId) {
			selected = checkpointId;
			paintSelection();
		},
		destroy() {
			toolbar.remove();
			list.remove();
			distanceLine.remove();
			status.remove();
		},
	};
}
