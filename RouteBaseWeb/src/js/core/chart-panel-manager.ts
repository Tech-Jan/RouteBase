/**
 * chart-panel-manager.ts — Shared Chart Panel Tab/Toggle System
 *
 * Manages a group of chart panels inside a wrapper element:
 *   - Show/hide individual panels via tab buttons (data-chart-tab attribute)
 *   - Double-rAF resize after every visibility change so Chart.js always
 *     reads the correct container width
 *   - Keeps at least one panel visible at all times
 *
 * Usage:
 *   const mgr = new ChartPanelManager({
 *       wrapperId:     'chartPanelWrap',   // id of the .chart-panel-wrap container
 *       xAxisGroupId:  'chartXAxisGroup',  // optional — id of the x-axis toggle group
 *       chartCardAttr: 'seg-charts',       // optional — data-collapsible-card value
 *       xAxisTabs:     ['speed', 'elevation'], // tabs that need the x-axis controls
 *       defaultTabs:   ['speed'],              // initially visible tabs
 *       chartMap: {                            // map of tab key → () => Chart instance
 *           speed:      () => ctx.performanceChart,
 *           elevation:  () => ctx.elevationChart,
 *           comparison: () => ctx.comparisonChart,
 *           progress:   () => ctx.progressChart
 *       }
 *   });
 *
 *   // Wire toggle buttons automatically (reads data-chart-tab attributes inside wrapper)
 *   mgr.initButtons();
 *
 *   // Or toggle programmatically:
 *   mgr.toggle('elevation');
 */
import type { ChartJsInstance } from './core-types';
import { asChart } from './chart-internals';
import { redecimateChartForRange } from './chart-factory';
import { reportChartError } from './chart-error-log';
import { afterLayout } from './after-layout';

/**
 * Loaded via core/core.ts (ES module entry in _Layout.cshtml).
 * Consumed by: activity-details.ts, segment-stats module.
 */

export interface ChartPanelManagerOpts {
		wrapperId?: string;
		xAxisGroupId?: string | null;
		chartCardAttr?: string | null;
		xAxisTabs?: string[];
		chartMap?: { [key: string]: () => ChartJsInstance | null };
		defaultTabs?: string[];
	}

	class ChartPanelManager {
		private _wrapperId: string;
		private _xAxisGroupId: string | null;
		private _chartCardAttr: string | null;
		private _xAxisTabs: string[];
		private _chartMap: { [key: string]: () => ChartJsInstance | null };
		// Internal active-tab state. All consumers now read via the activePanels() accessor
		// (CF-03/SC2-06) — a defensive-copy snapshot instead of this live array; no external
		// module captures `_active` directly anymore.
		_active: string[];
		private _scope: Element | Document;

		constructor(opts: ChartPanelManagerOpts) {
			this._wrapperId = opts.wrapperId || "chartPanelWrap";
			this._xAxisGroupId = opts.xAxisGroupId || null;
			this._chartCardAttr = opts.chartCardAttr || null;
			this._xAxisTabs = opts.xAxisTabs || [];
			this._chartMap = opts.chartMap || {};
			this._active = (opts.defaultTabs || []).slice();

			this._scope = document;
		}

		// ------------------------------------------------------------------
		// Public API
		// ------------------------------------------------------------------

		/** Wire all [data-chart-tab] buttons found near the wrapper. */
		initButtons(resetBtnId?: string): void {
			const wrap = this._getWrap();
			if (!wrap) return;

			// Scope button search to the closest card parent (or document as fallback)
			const scope: Element | Document =
				wrap.closest("[data-collapsible-card]") || wrap.closest(".card") || document;

			// Tab toggle buttons scoped to this card
			scope.querySelectorAll("[data-chart-tab]").forEach((btn) => {
				btn.addEventListener("click", () => {
					this.toggle((btn as Element).getAttribute("data-chart-tab") as string);
				});
			});

			// Store scope for _apply to use
			this._scope = scope;

			// Optional global "reset zoom" button
			if (resetBtnId) {
				const resetBtn = document.getElementById(resetBtnId);
				if (resetBtn) {
					resetBtn.addEventListener("click", (e: Event) => {
						e.preventDefault();
						this.resetAllZoom();
					});
				}
			}

			this._apply();
		}

		/** Toggle a panel by key. Always keeps at least one panel active. */
		toggle(key: string): void {
			const idx = this._active.indexOf(key);
			if (idx >= 0) {
				if (this._active.length === 1) return; // keep at least one
				this._active.splice(idx, 1);
			} else {
				this._active.push(key);
			}
			this._apply();
		}

		/** Programmatically set exactly which tabs are active. */
		setActive(keys: string[]): void {
			if (!keys || keys.length === 0) return;
			this._active = keys.slice();
			this._apply();
		}

		/** Currently-visible tab keys (snapshot — mutating the result has no effect on state). */
		activePanels(): string[] {
			return this._active.slice();
		}

		/** Reset zoom on all charts tracked by chartMap. */
		resetAllZoom(): void {
			Object.keys(this._chartMap).forEach((key) => {
				const chart = this._chartMap[key]?.(); // ?.() dead: key comes from Object.keys of this record
				if (chart && typeof chart.resetZoom === "function") {
					try {
						chart.resetZoom();
						// resetZoom restores the axis but leaves the dataset holding the old window's
						// decimation — re-decimate to the full range before rendering (CF-01). No-op for
						// non-factory charts.
						redecimateChartForRange(asChart(chart));
						chart.update("none");
					} catch (e) { reportChartError("chart-panel-manager:resetZoom:" + key, e); }
				}
			});
		}

		// ------------------------------------------------------------------
		// Private helpers
		// ------------------------------------------------------------------

		private _getWrap(): HTMLElement | null {
			return document.getElementById(this._wrapperId);
		}

		/** Apply current _active state to DOM + resize charts. Cross-module seam (see _active). */
		_apply(): void {
			const wrap = this._getWrap();
			if (!wrap) return;

			// Show/hide panels
			wrap.querySelectorAll(".chart-panel").forEach((p) => {
				const key = p.id.replace("panel-", "");
				p.classList.toggle("active", this._active.indexOf(key) >= 0);
			});

			// Highlight tab buttons (scoped to this card)
			const scope = this._scope || document;
			scope.querySelectorAll("[data-chart-tab]").forEach((btn) => {
				btn.classList.toggle(
					"active",
					this._active.indexOf(
						(btn as Element).getAttribute("data-chart-tab") as string,
					) >= 0,
				);
			});

			// X-axis control group visibility
			if (this._xAxisGroupId) {
				const needsXAxis = this._active.some((k) => this._xAxisTabs.indexOf(k) >= 0);
				const xGroup = document.getElementById(this._xAxisGroupId);
				if (xGroup) xGroup.style.display = needsXAxis ? "" : "none";
			}

			// Collapsible card x-axis class
			if (this._chartCardAttr) {
				const card = document.querySelector(
					'[data-collapsible-card="' + this._chartCardAttr + '"]',
				);
				if (card) {
					const anyXAxis = this._active.some((k) => this._xAxisTabs.indexOf(k) >= 0);
					card.classList.toggle("hide-xaxis", !anyXAxis);
				}
			}

			// Resize all active charts after browser paints the new layout
			this._resizeAfterLayout(this._active.slice());
		}

		/**
		 * Resize the active charts once layout has settled (shared afterLayout double-rAF).
		 * Cross-module seam (see _active).
		 */
		_resizeAfterLayout(keys: string[]): void {
			afterLayout(() => {
				keys.forEach((key) => {
					const getFn = this._chartMap[key];
					if (!getFn) return;
					const chart = getFn();
					if (!chart) return;
					try {
						chart.resize();
					} catch (e) {
						reportChartError("chart-panel-manager:resize:" + key, e);
					}
				});
			});
		}
	}

// Export for direct ESM imports.
export { ChartPanelManager };
