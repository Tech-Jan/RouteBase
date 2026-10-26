// statistics-year-row.ts — a second "year" tier drawn under the month labels.
//
// For the all-time / multi-year views the month ruler alone ("Jan Feb … Jan Feb …") loses the
// year. This plugin draws, just below the month tick labels, a short vertical tick at each
// January 1 boundary and the year centred within its visible span. It reads chart.scales.x at
// draw time, so it stays aligned under pan/zoom (a second time axis would not, since zoom only
// targets the "x" scale). Enable per-chart via options.plugins.yearRow = { show: true } and
// reserve room with layout.padding.bottom (≈ YEAR_ROW_HEIGHT).

import { cssVar } from "../core/css-vars";
import { scaleInternals } from '../core/chart-internals';

export interface YearRowOptions {
	show?: boolean;
}

// Vertical space (px) the row needs: tick + gap + label. Callers set layout.padding.bottom to this.
export const YEAR_ROW_HEIGHT = 20;

const TICK_LEN = 5;

export const yearRowPlugin: import("chart.js").Plugin = {
	id: "yearRow",
	afterDraw(chart, _args, opts: YearRowOptions): void {
		if (!opts?.show) return;
		const scale = chart.scales.x ? scaleInternals(chart.scales.x) : undefined;
		if (!scale || scale.min == null || scale.max == null || scale.bottom == null || !scale.getPixelForValue) return;

		const lo = scale.min;
		const hi = scale.max;
		const area = chart.chartArea;
		const c = chart.ctx;
		const tickTop = scale.bottom + 1; // just under the month tick labels
		const labelY = tickTop + TICK_LEN + 1;
		const color = cssVar("--fg-3", "#94a3b8");
		const mono = cssVar("--font-mono", "monospace");

		c.save();
		c.strokeStyle = color;
		c.fillStyle = color;
		c.font = "11px " + mono;
		c.textBaseline = "top";
		c.textAlign = "center";
		c.lineWidth = 1;

		const startYear = new Date(lo).getUTCFullYear();
		const endYear = new Date(hi).getUTCFullYear();
		for (let yr = startYear; yr <= endYear; yr++) {
			const yearStart = Date.UTC(yr, 0, 1);
			const yearEnd = Date.UTC(yr + 1, 0, 1);

			// Small vertical tick at the year boundary (if it falls inside the visible range).
			if (yearStart >= lo && yearStart <= hi) {
				const px = scale.getPixelForValue(yearStart);
				c.beginPath();
				c.moveTo(px, tickTop);
				c.lineTo(px, tickTop + TICK_LEN);
				c.stroke();
			}

			// Year label centred within the visible portion of the year, clamped to the plot area.
			const segLo = Math.max(lo, yearStart);
			const segHi = Math.min(hi, yearEnd);
			if (segHi > segLo) {
				const pxLo = Math.max(area.left, scale.getPixelForValue(segLo));
				const pxHi = Math.min(area.right, scale.getPixelForValue(segHi));
				if (pxHi - pxLo > 16) c.fillText(String(yr), (pxLo + pxHi) / 2, labelY);
			}
		}
		c.restore();
	},
};
