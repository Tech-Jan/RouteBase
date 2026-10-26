// trash.ts — the trash page: a card feed of what you deleted, with restore / delete permanently.
//
// The cards are server-rendered (Pages/Activities/Trash.cshtml wraps the dashboard's _ActivityCard); this
// module owns the moving parts: the why-lines, the minimaps, selection, and the actions — all against the
// owner-scoped /api/trash endpoints (TrashApiController).

import { readData } from '../core/data-attrs';
import { setupLazyThumbnails } from '../core/track-thumbnail';
import { readIsland } from '../core/page-bootstrap';
import { initTrashGrid } from './trash-grid';
import {
	purgeConfirmText,
	purgeDestroysAMerge,
	whyLine,
} from './trash-model';
import type { MergeSourcesConfirmBody, TrashedActivity, TrashGridBootstrap } from '../generated/contracts';

let items: TrashedActivity[] = [];

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

/** The page's rows, from the JSON island Razor writes (no window global — window-globals.d.ts). */
function readBootstrap(): TrashGridBootstrap | null {
	return readIsland<TrashGridBootstrap | null>('trashBootstrap', null);
}

function selectedIds(): number[] {
	return Array.from(document.querySelectorAll<HTMLInputElement>('.trash-row-check:checked'))
		.map((c) => Number(readData(c, 'trackId')))
		.filter((n) => Number.isFinite(n));
}

/** Fills each card's why-line from the shared pure logic — the same text the purge dialog will use. */
function renderWhyLines(): void {
	for (const item of items) {
		const why = document.querySelector<HTMLElement>(`[data-why-track-id="${item.trackId}"]`);
		if (!why) continue;
		why.textContent = whyLine(item);
		why.classList.toggle('trash-card-why--warn', item.mergedIntoId !== null);
	}
}

// The trash's own GPS endpoint: /api/activities/{id}/gps-summary is visible-gated (released only), so a
// trashed ride reads empty through it. This one gates on owned AND trashed.
function setupMinimaps(): void {
	setupLazyThumbnails({
		containerSelector: '.trash-card .minimap[data-track-id]',
		idAttribute: 'data-track-id',
		fetchGps: async (trackId) => {
			const resp = await fetch(`/api/trash/${trackId}/gps-summary`);
			if (!resp.ok) throw new Error(`GPS fetch failed: ${resp.status}`);
			return resp.json();
		},
		trackColor: '#22d3ee', // --acc-cyan
	});
}

function syncToolbar(): void {
	const ids = selectedIds();
	const restore = el<HTMLButtonElement>('trashRestoreBtn');
	const purgeBtn = el<HTMLButtonElement>('trashPurgeBtn');
	const label = el<HTMLSpanElement>('trashSelectionLabel');
	if (restore) restore.disabled = ids.length === 0;
	if (purgeBtn) purgeBtn.disabled = ids.length === 0;
	if (label) label.textContent = ids.length === 0 ? 'Select all' : `${ids.length} selected`;
}


async function post(path: string, body: object): Promise<Response | null> {
	try {
		return await fetch(`/api/trash/${path}`, {
			method: 'POST',
			headers: { 'Content-Type': 'application/json' },
			body: JSON.stringify(body),
		});
	} catch {
		return null;
	}
}

// Cards, rows and the filter strip all come from the server render — so a successful action reloads rather
// than trying to keep three client-side copies of the truth in step.
function reload(): void {
	window.location.reload();
}

async function restore(ids: number[]): Promise<void> {
	if (ids.length === 0) return;
	const resp = await post('restore', { trackIds: ids });
	if (resp?.ok) reload();
	// Say so — a silent no-op after a clicked Restore reads as "it worked but didn't refresh".
	else window.alert('Restore failed — check the connection and try again.');
}

async function purge(ids: number[], all: boolean): Promise<void> {
	if (!all && ids.length === 0) return;
	const confirmIds = all ? items.map((i) => i.trackId) : ids;
	if (!window.confirm(purgeConfirmText(items, confirmIds))) return;

	// Confirm only what the dialog above actually NAMED. If this page's snapshot saw no merge sources, the
	// flag stays false so the server's 409 still has teeth — a merge made in another tab since page load
	// then comes back as a warning with the server's own message, and the user re-confirms against THAT.
	const confirmed = purgeDestroysAMerge(items, confirmIds);
	const bodyFor = (c: boolean) => (all ? { confirmMergeSources: c } : { trackIds: ids, confirmMergeSources: c });
	const path = all ? 'empty' : 'purge';

	let resp = await post(path, bodyFor(confirmed));
	if (resp?.status === 409) {
		const payload = (await resp.json().catch(() => null)) as MergeSourcesConfirmBody | null;
		// Branch on the machine `code` (D32): it carries the "mergeSourcesNeedConfirm" token the redundant
		// `error` field used to hold; `message` is the human confirm text shown below.
		if (payload?.code !== 'mergeSourcesNeedConfirm') return;
		if (!window.confirm(`${payload.message ?? 'Some of these rides were merged into others.'}\n\nDelete permanently anyway?`))
			return;
		resp = await post(path, bodyFor(true));
	}
	if (resp?.ok) reload();
	// The user just confirmed a destructive dialog — a silent failure here is the worst outcome.
	else window.alert('Delete failed — nothing was removed. Check the connection and try again.');
}

function init(): void {
	const boot = readBootstrap();
	if (!boot) return; // empty trash — the page renders a message and no cards
	items = boot.rows;

	renderWhyLines();
	setupMinimaps();
	initTrashGrid(boot);

	document.querySelectorAll<HTMLInputElement>('.trash-row-check').forEach((c) => {
		c.addEventListener('change', syncToolbar);
	});

	el<HTMLInputElement>('trashSelectAll')?.addEventListener('change', (e) => {
		const on = (e.target as HTMLInputElement).checked;
		document.querySelectorAll<HTMLInputElement>('.trash-row-check').forEach((c) => {
			c.checked = on;
		});
		syncToolbar();
	});

	el<HTMLButtonElement>('trashRestoreBtn')?.addEventListener('click', () => void restore(selectedIds()));
	el<HTMLButtonElement>('trashPurgeBtn')?.addEventListener('click', () => void purge(selectedIds(), false));
	el<HTMLButtonElement>('trashEmptyBtn')?.addEventListener('click', () => void purge([], true));

	// Per-card actions — the same two verbs as the toolbar, scoped to one ride.
	document.querySelectorAll<HTMLButtonElement>('.trash-card-restore').forEach((b) => {
		b.addEventListener('click', () => void restore([Number(readData(b, 'trackId'))]));
	});
	document.querySelectorAll<HTMLButtonElement>('.trash-card-purge').forEach((b) => {
		b.addEventListener('click', () => void purge([Number(readData(b, 'trackId'))], false));
	});

	syncToolbar();
}

if (typeof document !== 'undefined') {
	if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', init);
	else init();
}
