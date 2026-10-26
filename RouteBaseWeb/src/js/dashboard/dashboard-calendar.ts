// dashboard-calendar.ts — Activity Calendar (heatmap) widget + month navigation + day popup.
//
// Owns calendarState. On every month change it fires onMonthChange(year, month) so the volume/KPI
// widgets refetch — decoupled via callback to avoid a calendar→volume/kpi import cycle.

import { readData } from '../core/data-attrs';
import { must } from '../core/invariant';
import { MONTH_NAMES, escHtml, replaceWidgetContent } from './dashboard-shared';
import { computeHeatmapDays, localDayIso, type HeatmapDay, type HeatmapDayActivity } from './dashboard-stats';
import type { DashboardGridRow } from '../generated/contracts';

const calendarState = {
	year: new Date().getFullYear(),
	month: new Date().getMonth() + 1,
};

let currentHeatmapData: HeatmapDay[] = [];
let activePopup: HTMLElement | null = null;
let activeOverlay: HTMLElement | null = null;

// Fired after every month change so the entry can repaint the volume + KPI widgets.
let onMonthChange: (year: number, month: number) => void = () => {};

// Reads the feed grid's current filtered rows — the calendar shows exactly the cards' set.
let getRows: () => DashboardGridRow[] = () => [];

function calTypeIcon(type: string): string {
	const f = "rgba(255,255,255,0.9)";
	// All icons: 17×17 rendered, 24×24 viewBox, stroke-based figure style
	const s = `fill="none" stroke="${f}" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"`;
	switch (type) {
		case "run":
			// Leaning person in mid-stride
			return `<svg width="17" height="17" viewBox="0 0 24 24" ${s} aria-hidden="true">
				<circle cx="15.5" cy="3.5" r="2.2" fill="${f}" stroke="none"/>
				<path d="M13.5,5.5 L10,11"/>
				<path d="M12,8 L16.5,10.5"/>
				<path d="M12,8 L8,6.5"/>
				<path d="M10,11 L13.5,17"/>
				<path d="M10,11 L7,15.5 L9.5,19"/>
			</svg>`;
		case "bike":
			// Two wheels + triangle frame + seat + handlebars
			return `<svg width="17" height="17" viewBox="0 0 24 24" ${s} aria-hidden="true">
				<circle cx="5" cy="17" r="4"/>
				<circle cx="19" cy="17" r="4"/>
				<path d="M5,17 L12,8 L19,17 L12,17 Z"/>
				<line x1="10" y1="8" x2="14" y2="8"/>
				<line x1="17" y1="8" x2="21" y2="8"/>
				<line x1="19" y1="13" x2="21" y2="8"/>
			</svg>`;
		case "walk":
			// Upright walking figure
			return `<svg width="17" height="17" viewBox="0 0 24 24" ${s} aria-hidden="true">
				<circle cx="12" cy="3.5" r="2.2" fill="${f}" stroke="none"/>
				<path d="M11.5,5.5 L10.5,13"/>
				<path d="M11,9 L15,12"/>
				<path d="M11,9 L7.5,7.5"/>
				<path d="M10.5,13 L12.5,20"/>
				<path d="M10.5,13 L7.5,19.5"/>
			</svg>`;
		case "multi":
			// Plus sign — multiple types
			return `<svg width="17" height="17" viewBox="0 0 24 24" ${s} aria-hidden="true">
				<line x1="12" y1="4" x2="12" y2="20"/>
				<line x1="4" y1="12" x2="20" y2="12"/>
			</svg>`;
		default:
			// Lightning bolt — generic activity fallback (skiing, swimming, etc.)
			return `<svg width="17" height="17" viewBox="0 0 24 24" fill="${f}" stroke="none" aria-hidden="true">
				<path d="M13,2 L5,13 L10.5,13 L11,22 L19,11 L13.5,11 Z"/>
			</svg>`;
	}
}

function computeActivityOpacities(days: HeatmapDay[]): Record<string, number> {
	const groups: Record<string, { iso: string; logDist: number }[]> = {};
	for (const d of days) {
		if (!d.activityCount) continue;
		const type = (d.activityTypes?.length ?? 0) > 1 ? "multi" : (d.activityTypes?.[0] ?? "other");
		if (!groups[type]) groups[type] = [];
		const totalKm = (d.activities ?? []).reduce((sum, a) => sum + (a.distanceKm ?? 0), 0);
		groups[type].push({ iso: d.date, logDist: Math.log1p(totalKm) });
	}

	const result: Record<string, number> = {};
	for (const entries of Object.values(groups)) {
		if (entries.length <= 1) {
			for (const e of entries) result[e.iso] = 1.0;
			continue;
		}
		const sorted = [...entries].sort((a, b) => a.logDist - b.logDist);
		const p10 = must(sorted[Math.floor(sorted.length * 0.1)], 'p10 index (floor(len*0.1) < len)').logDist;
		const p90 = must(sorted[Math.min(Math.floor(sorted.length * 0.9), sorted.length - 1)], 'p90 index (clamped to len-1)').logDist;
		const range = p90 - p10;
		for (const e of entries) {
			const t = range === 0 ? 1.0 : Math.min(1.0, Math.max(0.0, (e.logDist - p10) / range));
			result[e.iso] = 0.35 + 0.65 * t;
		}
	}
	return result;
}

function renderCalendarMonth(days: HeatmapDay[], year: number, month: number, opacities: Record<string, number>): string {
	const lookup: Record<string, HeatmapDay> = {};
	for (const d of days) lookup[d.date] = d;

	// Local on purpose: days are bucketed by the row's local start date, so the today-ring must match (DT-02).
	const todayISO = localDayIso(new Date());
	const daysInMonth = new Date(year, month, 0).getDate();
	// Monday-first: JS getDay() Sun=0..Sat=6 → Mon=0..Sun=6
	const leadingBlanks = (new Date(year, month - 1, 1).getDay() + 6) % 7;

	const dayHeaders = ["M","T","W","T","F","S","S"]
		.map((l) => `<div class="cal-day-header">${l}</div>`)
		.join("");

	const cells: string[] = [];
	for (let i = 0; i < leadingBlanks; i++) {
		cells.push(`<div class="cal-cell cal-cell--empty"></div>`);
	}

	for (let d = 1; d <= daysInMonth; d++) {
		const iso = `${year}-${String(month).padStart(2, "0")}-${String(d).padStart(2, "0")}`;
		const isToday = iso === todayISO;
		const isFuture = iso > todayISO;
		const todayCls = isToday ? " cal-cell--today" : "";

		if (isFuture) {
			cells.push(`<div class="cal-cell cal-cell--future">${d}</div>`);
			continue;
		}

		const dayData = lookup[iso];
		if (!dayData || dayData.activityCount === 0) {
			cells.push(`<div class="cal-cell${todayCls}">${d}</div>`);
			continue;
		}

		const types = dayData.activityTypes ?? [];
		const dominantType = types.length > 1 ? "multi" : (types[0] ?? "other");
		const label = dayData.activityCount === 1 ? "1 activity" : `${dayData.activityCount} activities`;
		const opacity = (opacities[iso] ?? 1.0).toFixed(2);

		cells.push(
			`<div class="cal-cell cal-cell--active cal-cell--${dominantType}${todayCls}" data-date="${iso}" title="${label}" style="opacity:${opacity}">` +
			calTypeIcon(dominantType) +
			`</div>`,
		);
	}

	// Pad to always fill 6 rows (42 cells) so the widget height stays constant
	while (cells.length < 42) {
		cells.push(`<div class="cal-cell cal-cell--empty"></div>`);
	}

	return (
		`<div class="cal-day-headers">${dayHeaders}</div>` +
		`<div class="cal-grid">${cells.join("")}</div>`
	);
}

function updateHeatmapNavState(): void {
	const now = new Date();
	const isCurrentMonth =
		calendarState.year === now.getFullYear() &&
		calendarState.month === now.getMonth() + 1;

	const nextBtn = document.getElementById("heatmap-next") as HTMLButtonElement | null;
	if (nextBtn) {
		nextBtn.disabled = isCurrentMonth;
		nextBtn.style.opacity = isCurrentMonth ? "0.3" : "";
		nextBtn.style.cursor = isCurrentMonth ? "default" : "";
	}

	// Year-next is disabled if jumping +12 months would exceed the current month
	const yearNextBtn = document.getElementById("heatmap-year-next") as HTMLButtonElement | null;
	if (yearNextBtn) {
		const nextYearTooFar =
			calendarState.year + 1 > now.getFullYear() ||
			(calendarState.year + 1 === now.getFullYear() && calendarState.month > now.getMonth() + 1);
		yearNextBtn.disabled = nextYearTooFar;
		yearNextBtn.style.opacity = nextYearTooFar ? "0.3" : "";
		yearNextBtn.style.cursor = nextYearTooFar ? "default" : "";
	}

	const todayBtn = document.getElementById("heatmap-today") as HTMLButtonElement | null;
	if (todayBtn) todayBtn.style.visibility = isCurrentMonth ? "hidden" : "visible";

	const label = document.getElementById("heatmap-month-label");
	if (label) {
		label.textContent = `${MONTH_NAMES[calendarState.month - 1]} ${calendarState.year}`;
	}
}

/** Paints the calendar for `year`/`month` (defaults: the currently selected month) from the filtered rows. */
export function renderHeatmap(year?: number, month?: number): void {
	const el = document.getElementById("heatmap-widget");
	if (!el) return;
	if (year !== undefined) calendarState.year = year;
	if (month !== undefined) calendarState.month = month;
	const { year: curYear, month: curMonth } = calendarState;
	updateHeatmapNavState();
	onMonthChange(curYear, curMonth); // repaint volume + KPI for this month

	currentHeatmapData = computeHeatmapDays(getRows(), curYear, curMonth);
	const inner = document.createElement("div");
	// eslint-disable-next-line no-unsanitized/property -- renderCalendarMonth interpolates numerics + the typeGroup enum only (S2 census)
	inner.innerHTML = renderCalendarMonth(currentHeatmapData, curYear, curMonth, computeActivityOpacities(currentHeatmapData));
	replaceWidgetContent(el, inner);
}

// ─── Heatmap day popup ────────────────────────────────────────────────────────

const DAY_NAMES = ["Sun","Mon","Tue","Wed","Thu","Fri","Sat"];
const MON_ABBR  = ["Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"];

function formatDuration(seconds: number): string {
	const h = Math.floor(seconds / 3600);
	const m = Math.floor((seconds % 3600) / 60);
	return h > 0 ? `${h}h ${m}m` : `${m}m`;
}

function formatPopupDate(iso: string): string {
	const d = new Date(iso + "T00:00:00");
	return `${DAY_NAMES[d.getDay()]}, ${d.getDate()} ${MON_ABBR[d.getMonth()]}`;
}

function closePopup(): void {
	activePopup?.remove();
	activeOverlay?.remove();
	activePopup = null;
	activeOverlay = null;
	document.removeEventListener("keydown", onPopupEsc);
	document.removeEventListener("click", onPopupOutsideClick);
}

function onPopupEsc(e: KeyboardEvent): void {
	if (e.key === "Escape") closePopup();
}

function onPopupOutsideClick(e: MouseEvent): void {
	if (activePopup && !activePopup.contains(e.target as Node)) closePopup();
}

function buildActivityRows(activities: HeatmapDayActivity[]): string {
	return activities.map((a) => {
		const parts: string[] = [];
		const km = a.distanceKm ?? 0;
		if (km > 0) parts.push(km.toFixed(1) + " km");
		const dur = a.durationSeconds ?? 0;
		if (dur > 0) parts.push(formatDuration(dur));
		const metrics = parts.length ? `<span class="hmp__metrics">${parts.join(" · ")}</span>` : "";
		return `<a href="/Activities/Details/${a.id}" class="hmp__row">` +
			`<span class="hmp__dot hmp__dot--${a.typeGroup}"></span>` +
			`<span class="hmp__name">${escHtml(a.name || "Activity")}</span>` +
			metrics +
			`</a>`;
	}).join("");
}

function showDayPopup(dayData: HeatmapDay, cellEl: HTMLElement): void {
	closePopup();
	const dateStr = formatPopupDate(dayData.date);
	const rows = buildActivityRows(dayData.activities);
	let totals = "";
	if (dayData.activities.length > 1) {
		const km  = dayData.activities.reduce((s, a) => s + (a.distanceKm ?? 0), 0);
		const sec = dayData.activities.reduce((s, a) => s + (a.durationSeconds ?? 0), 0);
		const p: string[] = [`${dayData.activities.length} activities`];
		if (km  > 0) p.push(km.toFixed(1) + " km");
		if (sec > 0) p.push(formatDuration(sec));
		totals = `<div class="hmp__totals">${p.join(" · ")}</div>`;
	}
	const inner = `<div class="hmp__date">${escHtml(dateStr)}</div>${rows}${totals}`;

	if (window.innerWidth < 480) {
		const overlay = document.createElement("div");
		overlay.className = "hmp-overlay";
		overlay.addEventListener("click", closePopup);
		const sheet = document.createElement("div");
		sheet.className = "hmp-sheet";
		// eslint-disable-next-line no-unsanitized/property -- inner built above: names/date escHtml'd, ids/metrics numeric, typeGroup server enum
		sheet.innerHTML = `<div class="hmp-sheet__handle"></div>${inner}`;
		document.body.appendChild(overlay);
		document.body.appendChild(sheet);
		activePopup = sheet;
		activeOverlay = overlay;
		requestAnimationFrame(() => {
			overlay.classList.add("hmp-overlay--in");
			sheet.classList.add("hmp-sheet--in");
		});
	} else {
		const popup = document.createElement("div");
		popup.className = "hmp-popup";
		// eslint-disable-next-line no-unsanitized/property -- inner built above: names/date escHtml'd, ids/metrics numeric, typeGroup server enum
		popup.innerHTML = `${inner}<div class="hmp-popup__arrow"></div>`;
		popup.style.visibility = "hidden";
		document.body.appendChild(popup);

		const rect  = cellEl.getBoundingClientRect();
		const popW  = popup.offsetWidth;
		const popH  = popup.offsetHeight;
		const gap   = 7;
		let top: number;
		let arrowSide: "bottom" | "top";
		if (rect.top - popH - gap > 0) {
			top = rect.top - popH - gap;
			arrowSide = "bottom";
		} else {
			top = rect.bottom + gap;
			arrowSide = "top";
		}
		let left = rect.left + rect.width / 2 - popW / 2;
		left = Math.max(8, Math.min(left, window.innerWidth - popW - 8));
		popup.style.top  = top  + "px";
		popup.style.left = left + "px";
		popup.style.visibility = "";
		popup.classList.add(`hmp-popup--arrow-${arrowSide}`);
		activePopup = popup;
		requestAnimationFrame(() => popup.classList.add("hmp-popup--in"));
	}
	document.addEventListener("keydown", onPopupEsc);
	setTimeout(() => document.addEventListener("click", onPopupOutsideClick), 0);
}

function initHeatmapNav(): void {
	document.getElementById("heatmap-year-prev")?.addEventListener("click", () => {
		renderHeatmap(calendarState.year - 1, calendarState.month);
	});

	document.getElementById("heatmap-prev")?.addEventListener("click", () => {
		let { year, month } = calendarState;
		month--;
		if (month < 1) { month = 12; year--; }
		renderHeatmap(year, month);
	});

	document.getElementById("heatmap-next")?.addEventListener("click", () => {
		const now = new Date();
		const { year, month } = calendarState;
		if (year === now.getFullYear() && month === now.getMonth() + 1) return;
		let ny = year, nm = month + 1;
		if (nm > 12) { nm = 1; ny++; }
		renderHeatmap(ny, nm);
	});

	document.getElementById("heatmap-year-next")?.addEventListener("click", () => {
		const now = new Date();
		let { year, month } = calendarState;
		year++;
		// clamp to current month if the jump would go past it
		if (year > now.getFullYear() || (year === now.getFullYear() && month > now.getMonth() + 1)) {
			year = now.getFullYear();
			month = now.getMonth() + 1;
		}
		renderHeatmap(year, month);
	});

	document.getElementById("heatmap-today")?.addEventListener("click", () => {
		const now = new Date();
		renderHeatmap(now.getFullYear(), now.getMonth() + 1);
	});

	document.getElementById("heatmap-widget")?.addEventListener("click", (e) => {
		const cell = (e.target as HTMLElement).closest<HTMLElement>("[data-date]");
		if (!cell) return;
		const dayData = currentHeatmapData.find((d) => d.date === readData(cell, 'date'));
		if (dayData) showDayPopup(dayData, cell);
	});
}

/** Wires the calendar nav and paints the current month. `onMonthChange` repaints volume + KPI. */
export function initCalendar(opts: {
	onMonthChange: (year: number, month: number) => void;
	getRows: () => DashboardGridRow[];
}): void {
	onMonthChange = opts.onMonthChange;
	getRows = opts.getRows;
	initHeatmapNav();
	renderHeatmap(); // uses calendarState defaults (current month), also kicks onMonthChange
}
