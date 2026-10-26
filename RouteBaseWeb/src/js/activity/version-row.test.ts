import { describe, it, expect, vi } from 'vitest';
import { buildVersionRow, renderVersionList, type VersionRowOptions } from './version-row';
import type { VersionItem } from './version-history-model';

/**
 * version-row.ts — zero test file before tonight, despite rendering the exact "lost un-merge door" button
 * this project already shipped as a P1 once (EditorSurfaces): "Undo this save" hung off a single
 * `edited_commit_id` badge that both reset and revert cleared. A dropped `onUndoSave` pass-through, or a
 * wrong id captured in the click closure (e.g. versionId instead of commitId), silently reintroduces it —
 * so the core assertions here are about exactly THAT wiring, not just "a button exists".
 */

function version(overrides: Partial<VersionItem> = {}): VersionItem {
	return {
		versionId: 1, seq: 1, isHead: false, createdAt: '2026-07-17T10:00:00.000Z',
		pointCount: 100, distanceMeters: 5000, ascentMeters: 50, durationSeconds: 1200,
		provenance: 'Imported', parentNames: [], sameAsParent: false, commitId: null,
		...overrides,
	};
}

describe('buildVersionRow — Undo this save (the un-merge door)', () => {
	it('offers it on seq 1 with a commit (a merge/split PRODUCT) — the row that used to lose it', () => {
		const onUndoSave = vi.fn();
		// Deliberately distinct commitId vs versionId — a mix-up in the click closure would still pass a
		// "some number" assertion but fail an exact-value one.
		const v = version({ versionId: 55, seq: 1, isHead: false, commitId: 77 });

		const row = buildVersionRow(v, { onUndoSave });
		const btn = row.querySelector('button.btn-cockpit') as HTMLButtonElement;

		expect(btn.textContent).toBe('Undo this save…');
		btn.click();
		expect(onUndoSave).toHaveBeenCalledWith(77, v); // commitId, not versionId — and the version itself
	});

	it('offers it on the head with a commit, even at a high seq', () => {
		const onUndoSave = vi.fn();
		const v = version({ versionId: 9, seq: 12, isHead: true, commitId: 41 });

		const row = buildVersionRow(v, { onUndoSave });
		row.querySelector('button.btn-cockpit')!.dispatchEvent(new Event('click'));

		expect(onUndoSave).toHaveBeenCalledWith(41, v);
	});

	it('does NOT offer it on a middle version (seq > 1, not head) — superseded, a chained revert must go newest→oldest', () => {
		const onUndoSave = vi.fn();
		const v = version({ seq: 2, isHead: false, commitId: 41 });

		const row = buildVersionRow(v, { onUndoSave });

		expect(row.textContent).not.toContain('Undo this save');
	});

	it('does NOT offer it when there is no commit (import/recording/restore/recompute)', () => {
		const onUndoSave = vi.fn();
		const v = version({ seq: 1, isHead: true, commitId: null });

		const row = buildVersionRow(v, { onUndoSave });

		expect(row.textContent).not.toContain('Undo this save');
	});

	it('is never offered at all when the caller passes no onUndoSave (read-only surfaces)', () => {
		const v = version({ seq: 1, isHead: true, commitId: 41 });

		const row = buildVersionRow(v, {});

		expect(row.textContent).not.toContain('Undo this save');
	});
});

describe('buildVersionRow — Restore', () => {
	it('offers it on any non-head version and passes the version, row, and button through', () => {
		const onRestore = vi.fn();
		const v = version({ versionId: 3, isHead: false });

		const row = buildVersionRow(v, { onRestore });
		const btn = Array.from(row.querySelectorAll('button')).find((b) => b.textContent === 'Restore')!;
		btn.click();

		expect(onRestore).toHaveBeenCalledWith(v, row, btn);
	});

	it('is never offered on the head — restoring what you are already on does nothing', () => {
		const onRestore = vi.fn();
		const v = version({ isHead: true });

		const row = buildVersionRow(v, { onRestore });

		expect(row.textContent).not.toContain('Restore');
	});
});

describe('buildVersionRow — checked-out (server would 409)', () => {
	it('renders both offered buttons disabled, with the reason as the title, and wires no click handler', () => {
		const onRestore = vi.fn();
		const onUndoSave = vi.fn();
		const v = version({ seq: 1, isHead: false, commitId: 9 });

		const row = buildVersionRow(v, { onRestore, onUndoSave, checkedOut: true });
		const buttons = Array.from(row.querySelectorAll('button.btn-cockpit')) as HTMLButtonElement[];

		expect(buttons).toHaveLength(2);
		for (const btn of buttons) {
			expect(btn.disabled).toBe(true);
			expect(btn.title).toContain('resume or discard');
			btn.click();
		}
		expect(onRestore).not.toHaveBeenCalled();
		expect(onUndoSave).not.toHaveBeenCalled();
	});
});

describe('buildVersionRow — row content', () => {
	it('marks the head row and labels it "current"', () => {
		const v = version({ seq: 4, isHead: true });

		const row = buildVersionRow(v);

		expect(row.className).toContain('version-row--head');
		expect(row.querySelector('.version-row-title')!.textContent).toContain('v4 — current');
	});

	it('names the merge/split parents in the subtitle when present', () => {
		const v = version({ provenance: 'Merged', parentNames: ['Morning Ride', 'Evening Ride'] });

		const row = buildVersionRow(v);

		expect(row.querySelector('.version-row-sub')!.textContent).toBe('Merged — Morning Ride, Evening Ride');
	});
});

describe('renderVersionList', () => {
	function items(n: number): VersionItem[] {
		return Array.from({ length: n }, (_, i) => version({
			versionId: i + 1, seq: i + 1, isHead: i === n - 1, sameAsParent: false,
		}));
	}

	it('renders only the visible slice and returns null when nothing is hidden', () => {
		const list = document.createElement('ul');

		const expander = renderVersionList(list, items(3));

		expect(list.children).toHaveLength(3);
		expect(expander).toBeNull();
	});

	it('returns an expander when versions are folded, and it reveals the full list on click', () => {
		const list = document.createElement('ul');
		const opts: VersionRowOptions = {};

		const expander = renderVersionList(list, items(8), opts); // VERSIONS_SHOWN=5 → 3 folded

		expect(list.children).toHaveLength(5);
		expect(expander).not.toBeNull();
		expect(expander!.textContent).toContain('Show all 8 versions (3 hidden)');

		expander!.click();

		expect(list.children).toHaveLength(8);
		expect(expander!.isConnected).toBe(false); // the expander removes itself once everything is shown
	});
});
