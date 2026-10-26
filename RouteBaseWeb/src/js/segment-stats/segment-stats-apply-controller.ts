/**
 * Segment Statistics — Efforts grid Apply controller.
 *
 * Sorting and filtering the efforts table used to push every change straight into the charts + map
 * (via ctx.setVisibleEffortIndices), which is expensive and made sorting/typing feel laggy. This
 * controller decouples the two: the table stays live (cheap, in-memory), while chart/map sync and
 * effort removal are batched behind an "Apply" button that only lights up when there's something to sync.
 */
import { ctx } from './segment-statistics';

function setEquals(a: Set<number>, b: Set<number>): boolean {
	if (a.size !== b.size) return false;
	for (const v of a) if (!b.has(v)) return false;
	return true;
}

export interface ApplyController {
	mountButton(toolbar: HTMLElement): void;
	/** Called whenever the table's filtered/visible state changes (filter, sort, visibility toggle). */
	noteTableState(filteredIds: number[], visibleSet: Set<number>): void;
	/** Toggles pending-removal (exclusion) for a row (keyed by gpsInSegmentId). Returns the new marked state. */
	toggleRemoval(gpsInSegmentId: number): boolean;
	isMarked(gpsInSegmentId: number): boolean;
	/** Item 25: toggles pending-restore for an excluded row (keyed by gpsInSegmentId). Returns the new marked state. */
	toggleRestore(gpsInSegmentId: number): boolean;
	isRestoreMarked(gpsInSegmentId: number): boolean;
}

export function createApplyController(): ApplyController {
	const pendingRemovals = new Set<number>();
	// Item 25: excluded efforts marked for restore (delete their exclusion row on Apply).
	const pendingRestores = new Set<number>();
	// Charts currently show every effort on initial load (ctx.effortVisibility is seeded to "all"), so the
	// applied baseline starts as "all efforts" — matches reality with no explicit initial sync needed.
	const lastAppliedVisible = new Set<number>(ctx.effortVisibility);

	let filteredIds: number[] = [];
	let visibleSet = new Set<number>();
	let applying = false;
	let btn: HTMLButtonElement | null = null;
	let badge: HTMLSpanElement | null = null;

	function effectiveSet(): Set<number> {
		return new Set(filteredIds.filter((id) => visibleSet.has(id)));
	}

	function isDirty(): boolean {
		return pendingRemovals.size > 0 || pendingRestores.size > 0 || !setEquals(effectiveSet(), lastAppliedVisible);
	}

	function refreshButton(): void {
		if (!btn || !badge) return;
		const dirty = isDirty();
		btn.disabled = !dirty;
		btn.classList.toggle('seg-apply-btn--dirty', dirty);
		const pending = pendingRemovals.size + pendingRestores.size;
		if (pending > 0) {
			badge.textContent = String(pending);
			badge.hidden = false;
		} else {
			badge.hidden = true;
		}
	}

	async function apply(): Promise<void> {
		if (applying || !btn) return;
		applying = true;
		btn.disabled = true;
		let willReload = false;
		try {
			// Defence in depth: the Excluded/actions columns and their delegation are not built at all
			// when this capability is off, so nothing can mark a row — but a crafted DOM must not be able
			// to reach an owner mutation either. The server gates on OwnedSegments regardless; this keeps
			// a share view from even attempting the call.
			if (ctx.config.canExcludeEfforts !== true) {
				pendingRemovals.clear();
				pendingRestores.clear();
			}
			if (pendingRemovals.size > 0 || pendingRestores.size > 0) {
				// Snapshot the marked ids at confirm time so a row marked mid-flight (the Sets are frozen
				// by toggleRemoval/toggleRestore while `applying`, but be explicit) can never be
				// mutated outside the confirmed count (SS-03).
				const removeIds = Array.from(pendingRemovals);
				const restoreIds = Array.from(pendingRestores);
				const n = removeIds.length;
				// Item 25: removal is now a DURABLE exclusion — only a confirm for removals (restores are
				// non-destructive). Copy reflects that the exclusion persists across re-analysis (was SS-04:
				// "reappears on re-analysis", now wrong).
				if (n > 0 && !confirm(`Exclude ${n} effort${n === 1 ? '' : 's'} from this segment?\n\nThis hides the effort${n === 1 ? '' : 's'} from the totals, charts and heatmap but keeps the activit${n === 1 ? 'y' : 'ies'}.\nThe exclusion is permanent and survives re-analysis — Restore it any time from the Excluded column.`)) {
					return;
				}
				let failures = 0;
				for (const id of removeIds) {
					try {
						const response = await fetch('/api/SegmentsApi/efforts/' + id, { method: 'DELETE' });
						if (!response.ok && response.status !== 204) failures++;
					} catch {
						failures++;
					}
				}
				for (const id of restoreIds) {
					try {
						const response = await fetch('/api/SegmentsApi/efforts/' + id + '/restore', { method: 'POST' });
						if (!response.ok && response.status !== 204) failures++;
					} catch {
						failures++;
					}
				}
				if (failures > 0) alert(`Failed to apply ${failures} of ${removeIds.length + restoreIds.length} change(s).`);
				willReload = true;
				location.reload();
				return;
			}

			ctx.setVisibleEffortIndices(Array.from(effectiveSet()));
			lastAppliedVisible.clear();
			effectiveSet().forEach((id) => lastAppliedVisible.add(id));
		} finally {
			if (!willReload) {
				applying = false;
				refreshButton();
			}
		}
	}

	return {
		mountButton(toolbar: HTMLElement): void {
			btn = document.createElement('button');
			btn.type = 'button';
			btn.className = 'grid-toolbar-btn seg-apply-btn';
			btn.title = 'Apply pending changes to the charts and map';
			btn.disabled = true;
			badge = document.createElement('span');
			badge.className = 'seg-apply-badge';
			badge.hidden = true;
			btn.innerHTML = '<i class="bi bi-check2-circle"></i> Apply ';
			btn.appendChild(badge);
			btn.addEventListener('click', () => void apply());
			toolbar.appendChild(btn);
		},
		noteTableState(nextFilteredIds: number[], nextVisibleSet: Set<number>): void {
			filteredIds = nextFilteredIds;
			visibleSet = nextVisibleSet;
			refreshButton();
		},
		toggleRemoval(gpsInSegmentId: number): boolean {
			// Frozen mid-apply: the confirmed batch already snapshotted its ids; button state
			// re-renders on the post-apply reload anyway (SS-03).
			if (applying) return pendingRemovals.has(gpsInSegmentId);
			const marked = !pendingRemovals.has(gpsInSegmentId);
			if (marked) pendingRemovals.add(gpsInSegmentId);
			else pendingRemovals.delete(gpsInSegmentId);
			refreshButton();
			return marked;
		},
		isMarked(gpsInSegmentId: number): boolean {
			return pendingRemovals.has(gpsInSegmentId);
		},
		toggleRestore(gpsInSegmentId: number): boolean {
			if (applying) return pendingRestores.has(gpsInSegmentId);
			const marked = !pendingRestores.has(gpsInSegmentId);
			if (marked) pendingRestores.add(gpsInSegmentId);
			else pendingRestores.delete(gpsInSegmentId);
			refreshButton();
			return marked;
		},
		isRestoreMarked(gpsInSegmentId: number): boolean {
			return pendingRestores.has(gpsInSegmentId);
		},
	};
}
