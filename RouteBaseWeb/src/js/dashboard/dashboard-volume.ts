// dashboard-volume.ts — Weekly Volume widget: 12-week stacked-bar window + rolling stats.

import { replaceWidgetContent } from './dashboard-shared';
import { must } from '../core/invariant';
import { computeWeeklyVolume, isoWeekMonday, type WeeklyVolumeEntry } from './dashboard-stats';
import type { DashboardGridRow } from '../generated/contracts';
export type { WeeklyVolumeEntry };

const TYPE_ORDER = ["run", "bike", "walk", "other"] as const;
const TYPE_COLORS: Record<string, string> = {
	run:   "var(--acc-amber)",
	bike:  "var(--acc-cyan)",
	walk:  "var(--acc-lime)",
	other: "var(--fg-2)",
};

export function computeVolumeOffset(targetYear: number, targetMonth: number): number {
	const today = new Date();
	const todayMonday = isoWeekMonday(today);

	// Find the first Monday that falls inside the target month
	const firstOfMonth = new Date(targetYear, targetMonth - 1, 1);
	const lastOfMonth  = new Date(targetYear, targetMonth, 0);
	const daysToFirstMonday = (8 - firstOfMonth.getDay()) % 7;
	const firstMonday = new Date(firstOfMonth);
	firstMonday.setDate(firstOfMonth.getDate() + daysToFirstMonday);

	// Count how many Mondays (= bold bars) fall inside the month
	let n = 0;
	const cur = new Date(firstMonday);
	while (cur <= lastOfMonth) { n++; cur.setDate(cur.getDate() + 7); }
	if (n === 0) return 0;

	// Place the bold block in the centre of the 12-bar window.
	// padLeft = bars to the left of the first bold bar.
	const padLeft = Math.floor((12 - n) / 2);

	// The rightmost bar is (11 - padLeft) weeks after the first bold Monday.
	const rightmost = new Date(firstMonday);
	rightmost.setDate(firstMonday.getDate() + (11 - padLeft) * 7);

	// Can't show future weeks — fall back to current week as rightmost bar.
	if (rightmost >= todayMonday) return 0;

	const msPerWeek = 7 * 24 * 60 * 60 * 1000;
	return Math.round((todayMonday.getTime() - rightmost.getTime()) / msPerWeek);
}

// DT-02: "YYYY-MM-DD" parsed via new Date(str) is UTC midnight; reading it back with local
// getters shifts the day for UTC-minus users. Parse as LOCAL midnight to keep the calendar day.
export function parseIsoDateLocal(iso: string): Date {
	return new Date(iso + "T00:00:00");
}

export function isWeekInMonth(weekStart: string, year: number, month: number): boolean {
	const d = parseIsoDateLocal(weekStart);
	return d.getFullYear() === year && d.getMonth() + 1 === month;
}

export function formatVolumeRange(weeks: WeeklyVolumeEntry[]): string {
	if (weeks.length === 0) return "";
	const MON = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];
	const first = parseIsoDateLocal(must(weeks[0], 'non-empty (guarded above)').weekStart);
	const last  = parseIsoDateLocal(must(weeks[weeks.length - 1], 'non-empty (guarded above)').weekStart);
	if (first.getFullYear() === last.getFullYear()) {
		if (first.getMonth() === last.getMonth())
			return `${MON[first.getMonth()]} ${first.getFullYear()}`;
		return `${MON[first.getMonth()]}–${MON[last.getMonth()]} ${last.getFullYear()}`;
	}
	return `${MON[first.getMonth()]} ${String(first.getFullYear()).slice(2)}–${MON[last.getMonth()]} ${last.getFullYear()}`;
}

function updateVolumeNavState(weeks: WeeklyVolumeEntry[]): void {
	const label = document.getElementById("volume-period-label");
	if (label) label.textContent = formatVolumeRange(weeks);
}

// Bolds the weeks that fall inside the selected calendar month (year/month).
function renderVolumeBars(weeks: WeeklyVolumeEntry[], year: number, month: number): string {
	if (weeks.length === 0) return "";
	const max = Math.max(...weeks.map((w) => w.distanceKm), 0.1);
	const gap = 2;
	// DT-13: intentional — phones (≤480) render wide bars in a horizontal-scroll strip; the
	// narrow 5px is only the ≤768 tablet band where all 12 bars must fit without scrolling.
	// Computed at render time (not re-rendered on resize; overflow-x scroll absorbs it).
	const barW = window.innerWidth <= 480 ? 14 : window.innerWidth <= 768 ? 5 : 14;
	const barAreaH = 44;
	const labelH = 14;
	const svgW = weeks.length * (barW + gap) - gap;
	const svgH = barAreaH + labelH + 2;

	const rects = weeks
		.map((w, i) => {
			const x = i * (barW + gap);
			const inMonth = isWeekInMonth(w.weekStart, year, month);
			const label = `<text x="${x + barW / 2}" y="${barAreaH + labelH}" text-anchor="middle" ` +
				`font-size="8" fill="${inMonth ? "var(--fg-0)" : "var(--fg-3)"}" font-weight="${inMonth ? "bold" : "normal"}" font-family="var(--font-mono)">${w.weekLabel}</text>`;

			const nonZero = TYPE_ORDER
				.map((t) => ({ type: t, km: w.byType?.[t] ?? 0 }))
				.filter((s) => s.km > 0);

			if (nonZero.length === 0) {
				// No per-type data — fallback to a dimmed bar using total
				const h = Math.max(2, Math.round((w.distanceKm / max) * barAreaH));
				return (
					`<rect x="${x}" y="${barAreaH - h}" width="${barW}" height="${h}" rx="2" fill="var(--fg-3)" opacity="0.4"/>` +
					label
				);
			}

			const totalKm = nonZero.reduce((s, t) => s + t.km, 0);
			const totalH  = Math.max(2, Math.round((totalKm / max) * barAreaH));

			// Distribute heights proportionally; last segment absorbs rounding
			let assigned = 0;
			const heights = nonZero.map((t, j) => {
				if (j === nonZero.length - 1) return totalH - assigned;
				const h = Math.round((t.km / totalKm) * totalH);
				assigned += h;
				return h;
			});

			let yBase = barAreaH;
			let bars = "";
			for (let j = 0; j < nonZero.length; j++) {
				const h = must(heights[j], 'heights[j] lockstep with nonZero');
				const entry = must(nonZero[j], 'nonZero[j] (loop-bounded)');
				if (h <= 0) continue;
				yBase -= h;
				bars += `<rect x="${x}" y="${yBase}" width="${barW}" height="${h}" rx="1" fill="${TYPE_COLORS[entry.type]}" opacity="0.82"/>`;
			}
			return bars + label;
		})
		.join("");

	return `<svg width="${svgW}" height="${svgH}" viewBox="0 0 ${svgW} ${svgH}" overflow="visible">${rects}</svg>`;
}

/** Paints the 12-week window at `offset` from the filtered rows, bolding weeks in the selected month. */
export function renderWeeklyVolume(
	rows: DashboardGridRow[], offset: number, year: number, month: number,
): void {
	const el = document.getElementById("weekly-volume-widget");
	if (!el) return;

	const data = computeWeeklyVolume(rows, offset);
	updateVolumeNavState(data.weeks);

	const inner = document.createElement("div");
	// eslint-disable-next-line no-unsanitized/property -- all numeric metrics + the computed W{ISO} label only (S2 census)
	inner.innerHTML = `
		<div style="overflow-x:auto;padding-bottom:4px;min-height:60px;">${renderVolumeBars(data.weeks, year, month)}</div>
		<div style="margin-top:16px;padding-top:14px;border-top:1px solid var(--line);">
			<div class="cal-month-label mono" style="margin-bottom:8px;">12-week window</div>
			<div style="display:flex;justify-content:space-between;">
				<div class="mini-stat">
					<div class="mini-stat-value tnum">${data.avgWeeklyDistanceKm.toFixed(1)}<span class="mini-stat-unit">km</span></div>
					<div class="mini-stat-label">Avg/week</div>
				</div>
				<div class="mini-stat">
					<div class="mini-stat-value tnum">${data.longestWeekKm.toFixed(1)}<span class="mini-stat-unit">km</span></div>
					<div class="mini-stat-label">Best week</div>
				</div>
				<div class="mini-stat">
					<div class="mini-stat-value tnum">${Math.round(data.totalElevationM).toLocaleString()}<span class="mini-stat-unit">m</span></div>
					<div class="mini-stat-label">Total elev</div>
				</div>
			</div>
		</div>`;

	replaceWidgetContent(el, inner);
}
