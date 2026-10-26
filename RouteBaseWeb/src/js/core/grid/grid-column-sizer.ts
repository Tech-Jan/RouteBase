/**
 * Reusable column sizing for the grid.
 *
 * Two jobs, both generic (driven only by column keys, so any GridColumn schema / any table benefits):
 *  1. Lock column widths via `table-layout: fixed` + a `<colgroup>`, so cell content changing on
 *     filter/sort no longer reflows column widths (kills the width "flicker").
 *  2. Let the user drag a column's right border to resize it.
 *
 * Widths are seeded from each column's explicit `width`, else measured from the natural (content)
 * layout on first render. Resized widths are optionally persisted per-table as a DEVICE preference —
 * a column width is about one screen's pixels, so core/device-prefs.ts owns the key and the try/catch.
 */

import type { GridColumn } from './grid-types';
import { devicePrefKeys, getJson, removeDevicePref, setJson } from '../device-prefs';

const MIN_WIDTH = 48;

export class GridColumnSizer<TRow> {
	private readonly table: HTMLTableElement;
	private readonly colgroup: HTMLElement;
	private readonly widths = new Map<string, number>();
	/** Keys whose width the user set explicitly (dragged, or loaded from persistence) — never auto-cleared. */
	private readonly userSet = new Set<string>();
	private readonly persistKey?: string | undefined;
	private lastCols: GridColumn<TRow>[] = [];

	constructor(table: HTMLTableElement, persistKey?: string) {
		this.table = table;
		this.persistKey = persistKey;
		this.colgroup = document.createElement('colgroup');
		this.table.insertBefore(this.colgroup, this.table.firstChild);
		this.loadPersisted();
	}

	/** Rebuild the `<col>` list to match the currently visible columns, applying any known widths. */
	syncColumns(cols: GridColumn<TRow>[]): void {
		this.lastCols = cols;
		this.colgroup.innerHTML = '';
		for (const col of cols) {
			const c = document.createElement('col');
			const w = this.widths.get(col.key) ?? col.width;
			if (w) c.style.width = `${w}px`;
			this.colgroup.appendChild(c);
		}
		this.applyMinWidth();
	}

	/**
	 * Set the table's min-width to the sum of column widths so that, when columns total more than the
	 * container, the table overflows (→ horizontal scroll via the wrapper) instead of squeezing columns.
	 * The table keeps `width: 100%`, so when columns fit, the slack is distributed as before.
	 */
	private applyMinWidth(): void {
		let sum = 0;
		for (const col of this.lastCols) sum += this.widths.get(col.key) ?? col.width ?? 0;
		this.table.style.minWidth = sum > 0 ? `${sum}px` : '';
	}

	/**
	 * Ensure every visible column has a stored width (measuring from the current layout for any that don't),
	 * then switch the table to fixed layout and apply the widths. Idempotent: once all widths are known this
	 * just re-applies the colgroup. Call after a render that has real rows so measurements are accurate.
	 */
	ensureWidths(cols: GridColumn<TRow>[], headerCells: HTMLElement[]): void {
		// Seed any explicit widths first (no measurement needed).
		for (const col of cols) {
			if (!this.widths.has(col.key) && col.width) this.widths.set(col.key, col.width);
		}

		const needsMeasure = cols.some((col) => !this.widths.has(col.key));
		if (needsMeasure) {
			// Measure missing columns at their *natural* width: drop fixed layout + col widths so content
			// dictates, otherwise a just-toggled column would be measured while squeezed into fixed layout.
			this.table.style.tableLayout = 'auto';
			this.table.style.minWidth = '';
			this.colgroup.querySelectorAll('col').forEach((c) => ((c as HTMLElement).style.width = ''));
			cols.forEach((col, i) => {
				if (this.widths.has(col.key)) return;
				const measured = Math.round(headerCells[i]?.getBoundingClientRect().width ?? 0);
				if (measured > 0) this.widths.set(col.key, Math.max(MIN_WIDTH, measured));
			});
		}

		this.table.style.tableLayout = 'fixed';
		this.syncColumns(cols);
	}

	/**
	 * Drop auto-measured widths (keeping any the user explicitly dragged / persisted) so the next
	 * {@link ensureWidths} re-measures the remaining columns and re-fits them to the container. Call when the
	 * visible column set changes, so adding/removing a column redistributes widths instead of overflowing
	 * (which only resolved on a full reload, since the empty width map there re-measured everything at once).
	 */
	clearMeasuredWidths(): void {
		for (const key of [...this.widths.keys()]) {
			if (!this.userSet.has(key)) this.widths.delete(key);
		}
	}

	/**
	 * Forget all column widths (including user-dragged/persisted ones), clear persistence, and drop back to
	 * auto layout so the next {@link ensureWidths} re-measures from scratch. Used by the toolbar's "Reset all".
	 */
	reset(): void {
		this.widths.clear();
		this.userSet.clear();
		this.table.style.tableLayout = 'auto';
		this.table.style.minWidth = '';
		this.colgroup.querySelectorAll('col').forEach((c) => ((c as HTMLElement).style.width = ''));
		const k = this.storageKey();
		if (k) removeDevicePref(k);
	}

	/** Add a drag handle to a header cell that resizes its column (by index into the visible columns). */
	attachHandle(th: HTMLElement, col: GridColumn<TRow>, colIndex: number): void {
		const handle = document.createElement('span');
		handle.className = 'grid-col-resize';
		handle.setAttribute('aria-hidden', 'true');
		// Stop the header's sort click from firing when the user grabs the handle.
		handle.addEventListener('click', (e) => e.stopPropagation());
		handle.addEventListener('pointerdown', (e) => this.startResize(e, th, col, colIndex));
		th.appendChild(handle);
	}

	private startResize(e: PointerEvent, th: HTMLElement, col: GridColumn<TRow>, colIndex: number): void {
		e.preventDefault();
		e.stopPropagation();
		this.table.style.tableLayout = 'fixed';
		const colEl = this.colgroup.children[colIndex] as HTMLElement | undefined;
		const startX = e.clientX;
		const startW = this.widths.get(col.key) ?? Math.round(th.getBoundingClientRect().width);

		const onMove = (ev: PointerEvent): void => {
			const w = Math.max(MIN_WIDTH, Math.round(startW + (ev.clientX - startX)));
			this.widths.set(col.key, w);
			this.userSet.add(col.key);
			if (colEl) colEl.style.width = `${w}px`;
			this.applyMinWidth();
		};
		const onUp = (): void => {
			document.removeEventListener('pointermove', onMove);
			document.removeEventListener('pointerup', onUp);
			document.removeEventListener('pointercancel', onUp);
			document.body.classList.remove('grid-resizing');
			this.persist();
		};
		document.addEventListener('pointermove', onMove);
		document.addEventListener('pointerup', onUp);
		// A cancelled touch/pen drag never fires pointerup — end the resize the same way, keeping the
		// last width, so the move handler and the body class can't outlive the gesture.
		document.addEventListener('pointercancel', onUp);
		document.body.classList.add('grid-resizing');
	}

	private storageKey(): string | undefined {
		return this.persistKey ? devicePrefKeys.gridColumnWidths(this.persistKey) : undefined;
	}

	private loadPersisted(): void {
		const k = this.storageKey();
		if (!k) return;
		const raw = getJson<Record<string, unknown>>(k, {});
		for (const [key, w] of Object.entries(raw)) {
			if (typeof w === 'number' && w > 0) {
				this.widths.set(key, w);
				this.userSet.add(key);
			}
		}
	}

	private persist(): void {
		const k = this.storageKey();
		if (!k) return;
		// Persist only user-dragged widths: persisting auto-measured ones would pin every column as
		// user-set on reload, so the re-fit pass on column add/remove could never redistribute space.
		const obj: Record<string, number> = {};
		this.widths.forEach((w, key) => {
			if (this.userSet.has(key)) obj[key] = w;
		});
		setJson(k, obj);
	}
}
