// chart-readout-box.ts — shared floating "readout" box for chart crosshairs.
//
// A small, tight value box that floats just above a chart's vertical crosshair line and tracks the
// hovered x. Used by the Statistics cumulative chart, the Activity Details charts and the Segment
// Statistics charts. It is deliberately decoupled from Chart.js tooltips: callers build the rows
// themselves and pass the crosshair pixel x plus the plot's top edge, so it works equally well from a
// Chart.js external-tooltip handler, an `onHover` callback, or an index-based highlight path.
//
// The box is appended lazily into a host element which MUST be `position: relative` (and should allow
// overflow so the box can float above the plot). Styling lives in cockpit-components.css (.chart-readout*).
import { readData } from './data-attrs';
import { escapeHtml } from './html';

/** A single value column: number (right-aligned) + unit. `numWidthCh` fixes the number column width
 *  (in ch) so the box never reflows as values change while hovering. */
export interface ReadoutCell { num: string; unit: string; numWidthCh?: number | undefined; }

/** One row: a colour swatch + label, followed by one or more value cells. `emphasis` bolds the row.
 *  `labelWidthCh` reserves a fixed monospace width (in ch) for the label so the box never reflows when
 *  the label text changes (e.g. swapping a date for a time). `rowKey` is a stable identity (effort
 *  index, series label, …) emitted as `data-row-key` so hover/click handlers can tell which row. */
export interface ReadoutRow { color: string; label: string; cells: ReadoutCell[]; emphasis?: boolean | undefined; labelWidthCh?: number | undefined; rowKey?: string | undefined; }

/** Row interaction callbacks. `onRowHover` fires with the hovered row's key, or null when the pointer
 *  leaves all rows (but is still inside the box). `onRowClick` fires with the clicked row's key.
 *  `onHide` fires once when the box transitions from visible to hidden — callers use it to tear down
 *  the associated crosshair/map markers, so teardown is deferred until the sticky box actually closes
 *  (entering the box cancels the pending hide, keeping markers alive while a row is picked). */
export interface ReadoutInteractive {
	onRowHover?: (key: string | null) => void;
	onRowClick?: (key: string) => void;
	onHide?: () => void;
}

const GAP = 8; // px gap between the box's bottom and the plot's top edge

// Touch devices have no mouseleave to dismiss the box — render a ✕ button instead.
const _COARSE_POINTER =
	typeof window !== "undefined" &&
	typeof window.matchMedia === "function" &&
	window.matchMedia("(pointer: coarse)").matches;

export class ChartReadoutBox {
	private _host: HTMLElement;
	private _box: HTMLElement | null = null;
	private _interactive: ReadoutInteractive | null = null;
	private _onClose: (() => void) | null = null;
	private _pointerInside = false;
	private _hideTimer: number | null = null;
	private _hoveredKey: string | null = null;

	/** @param host position:relative container the box is appended into (e.g. a chart wrap/container). */
	constructor(host: HTMLElement) {
		this._host = host;
	}

	private _ensure(): HTMLElement {
		if (!this._box) {
			let box = this._host.querySelector<HTMLElement>(":scope > .chart-readout");
			if (!box) {
				box = document.createElement("div");
				box.className = "chart-readout";
				this._host.appendChild(box);
			}
			this._box = box;
			// ✕ dismiss (touch only) — delegated because show() rebuilds innerHTML each frame.
			// Fires the same teardown as the sticky close (onHide), plus the setOnClose callback
			// for passive boxes that never call setInteractive (Activity Details).
			box.addEventListener("click", (e) => {
				if (!(e.target as HTMLElement | null)?.closest(".chart-readout-close")) return;
				e.stopPropagation();
				this.hide();
				this._interactive?.onHide?.();
				this._onClose?.();
			});
		}
		return this._box;
	}

	/** Teardown callback for the touch ✕ button — for boxes without setInteractive (its onHide
	 *  serves the same role on interactive boxes). */
	setOnClose(cb: () => void): void {
		this._onClose = cb;
	}

	/**
	 * Enable hover/click on rows. Wires delegated listeners ONCE on the box element (rows are rebuilt as
	 * innerHTML every frame, so per-row listeners would leak). While the pointer is over the box, hover
	 * is sticky: any pending scheduleHide() is cancelled so reaching for a row doesn't dismiss it.
	 */
	setInteractive(opts: ReadoutInteractive): void {
		const box = this._ensure();
		box.classList.add("is-interactive");
		const wasWired = !!this._interactive;
		this._interactive = opts;
		if (wasWired) return; // listeners already attached; just swapped the callbacks

		box.addEventListener("mouseenter", () => { this._pointerInside = true; this.cancelHide(); });
		box.addEventListener("mouseleave", () => {
			this._pointerInside = false;
			this._setHovered(null);
			this.scheduleHide();
		});
		box.addEventListener("mousemove", (e) => {
			const row = (e.target as HTMLElement | null)?.closest<HTMLElement>(".chart-readout-row");
			this._setHovered(row ? readData(row, "rowKey") : null);
		});
		box.addEventListener("click", (e) => {
			const row = (e.target as HTMLElement | null)?.closest<HTMLElement>(".chart-readout-row");
			const key = row ? readData(row, "rowKey") : null;
			if (key != null) this._interactive?.onRowClick?.(key);
		});
	}

	private _setHovered(key: string | null): void {
		if (key === this._hoveredKey) return;
		this._hoveredKey = key;
		this._interactive?.onRowHover?.(key);
	}

	/** True while the pointer is over the box — callers skip hiding on chart-leave when this holds. */
	isPointerInside(): boolean {
		return this._pointerInside;
	}

	cancelHide(): void {
		if (this._hideTimer != null) { clearTimeout(this._hideTimer); this._hideTimer = null; }
	}

	/**
	 * Hide after a short grace period unless the pointer reaches the box first (cancelHide on enter).
	 * This is the sticky-dismiss path: when it actually closes a visible box, it fires onHide so callers
	 * can tear down the crosshair/markers. Plain hide() stays silent (used to swap which chart owns the
	 * box, or to clear a stale box on rebuild) so it never triggers teardown mid-hover.
	 */
	scheduleHide(delayMs = 80): void {
		this.cancelHide();
		this._hideTimer = window.setTimeout(() => {
			this._hideTimer = null;
			if (this._pointerInside) return;
			const wasVisible = !!this._box && this._box.style.display !== "none";
			this.hide();
			if (wasVisible) this._interactive?.onHide?.();
		}, delayMs);
	}

	hide(): void {
		this.cancelHide();
		this._hoveredKey = null; // reset silently — firing onRowHover here could re-render and resurrect the box
		// A hidden box can't contain the pointer. On touch, tapping a row synthesizes mouseenter
		// but the matching mouseleave never comes once the box is display:none — without this
		// reset the stale flag would veto every later scheduleHide.
		this._pointerInside = false;
		if (this._box) this._box.style.display = "none";
	}

	/**
	 * Render the box and position it centered on the crosshair, floating just above the plot.
	 * @param title   small header line (timestamp / date / x-position)
	 * @param rows    one row per series/metric
	 * @param caretX  crosshair x in canvas/host pixels
	 * @param plotTop chart plot area's top edge (chart.chartArea.top), in canvas/host pixels
	 */
	show(title: string, rows: ReadoutRow[], caretX: number, plotTop: number): void {
		const box = this._ensure();
		if (!rows.length) { box.style.display = "none"; return; }
		this.cancelHide(); // a fresh render means activity — don't let a pending hide fire

		const rowsHtml = rows.map((row) => {
			const cells = row.cells
				.map((c) => `<span class="cell"><span class="num"${c.numWidthCh ? ` style="min-width:${c.numWidthCh}ch"` : ""}>${escapeHtml(c.num)}</span><span class="unit">${escapeHtml(c.unit)}</span></span>`)
				.join("");
			const nameStyle = row.labelWidthCh
				? ` style="min-width:${row.labelWidthCh}ch;font-family:var(--font-mono)"`
				: "";
			const keyAttr = row.rowKey != null ? ` data-row-key="${escapeHtml(row.rowKey)}"` : "";
			return `<div class="chart-readout-row${row.emphasis ? " is-sel" : ""}"${keyAttr}><span class="swatch" style="background:${escapeHtml(row.color)}"></span>`
				+ `<span class="name"${nameStyle}>${escapeHtml(row.label)}</span>${cells}</div>`;
		}).join("");
		const closeHtml = _COARSE_POINTER
			? `<button type="button" class="chart-readout-close" aria-label="Dismiss">&times;</button>`
			: "";
		// eslint-disable-next-line no-unsanitized/property -- closeHtml is a literal ternary; rowsHtml rows escapeHtml every field above
		box.innerHTML = `${closeHtml}<div class="chart-readout-title">${escapeHtml(title)}</div>${rowsHtml}`;
		box.style.display = "block"; // CSS default is display:none

		// Measure at left:0 first. An absolutely-positioned box's shrink-to-fit width is limited by the
		// space from `left` to the containing block's right edge — so measuring while `left` is still large
		// (from a previous frame near the right edge) would squeeze the box and wrap its rows. Resetting to
		// 0 gives the full available width, so offsetWidth/Height reflect the true content size.
		box.style.left = "0px";
		const boxW = box.offsetWidth, boxH = box.offsetHeight;
		const maxLeft = Math.max(0, this._host.clientWidth - boxW);
		const left = Math.max(0, Math.min(caretX - boxW / 2, maxLeft));
		const top = plotTop - GAP - boxH;
		box.style.left = `${left.toFixed(1)}px`;
		box.style.top = `${top.toFixed(1)}px`;
	}
}
