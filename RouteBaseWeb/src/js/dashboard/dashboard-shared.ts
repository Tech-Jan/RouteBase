// dashboard-shared.ts — small helpers + constants shared across the dashboard widget modules.

// DT-12: the canonical attribute-safe escaper now lives in core/html; re-exported as escHtml.
export { escapeHtml as escHtml } from '../core/html';

export const MONTH_NAMES = [
	"January","February","March","April","May","June",
	"July","August","September","October","November","December",
];

export const WEEKDAY_NAMES = [
	"Sunday","Monday","Tuesday","Wednesday","Thursday","Friday","Saturday",
];

/** Replace all widget skeleton children, keeping the header element. */
export function replaceWidgetContent(el: HTMLElement, inner: HTMLElement): void {
	const header = el.querySelector(".widget-header");
	el.innerHTML = "";
	if (header) el.appendChild(header);
	el.appendChild(inner);
}
