/**
 * collapsible-card.ts — Reusable expand/collapse for chart cards.
 *
 * Extracted from the old utils god-file (CB-01 dissolution).
 *
 * Usage in HTML:
 *   <div class="card" data-collapsible-card="my-unique-key">
 *     <div class="card-header" data-collapsible-toggle>
 *       <h5>Title <i class="bi collapsible-chevron"></i></h5>
 *     </div>
 *     <div class="card-body" data-collapsible-body>
 *       ...content...
 *     </div>
 *   </div>
 *
 * Call initCollapsibleCards() after DOM is ready. Collapse state is a DEVICE preference (it is about one
 * screen's layout, so inheriting it on a new device would be wrong) — core/device-prefs.ts owns the key
 * and the try/catch.
 */

import { afterLayout } from "./after-layout";
import { devicePrefKeys, getBool, setBool } from "./device-prefs";

/** @private Apply the collapse/expand visual state */
function _applyCollapseState(
	body: HTMLElement,
	chevron: HTMLElement,
	collapsed: boolean,
): void {
	body.style.display = collapsed ? "none" : "";
	chevron.classList.toggle("bi-chevron-down", !collapsed);
	chevron.classList.toggle("bi-chevron-right", collapsed);
}

/**
 * Initialize all collapsible cards on the page.
 * Safe to call multiple times — already-initialized cards are skipped.
 */
export function initCollapsibleCards(): void {
	const cards = document.querySelectorAll(
		"[data-collapsible-card]:not([data-collapsible-initialized])",
	);
	cards.forEach((card) => {
		const key = card.getAttribute("data-collapsible-card");
		const toggle = card.querySelector("[data-collapsible-toggle]") as HTMLElement | null;
		const body = card.querySelector("[data-collapsible-body]") as HTMLElement | null;
		if (!toggle || !body) return;

		card.setAttribute("data-collapsible-initialized", "true");

		// Ensure chevron icon exists
		let chevron = toggle.querySelector(".collapsible-chevron") as HTMLElement | null;
		if (!chevron) {
			chevron = document.createElement("i");
			chevron.className = "bi collapsible-chevron";
			toggle.appendChild(chevron);
		}
		const chevronEl = chevron; // const rebind — the narrowing survives into the click closure

		// Apply the stored state. Expanded is the default: a card nobody has collapsed shows its content.
		const prefKey = devicePrefKeys.cardCollapsed(key ?? "");
		_applyCollapseState(body, chevronEl, getBool(prefKey, false));

		// Toggle on click
		toggle.style.cursor = "pointer";
		toggle.addEventListener("click", (e: Event) => {
			// Don't toggle when clicking buttons/links inside the header
			if ((e.target as Element).closest("button, a, input, select")) return;

			const nowCollapsed = body.style.display !== "none";
			setBool(prefKey, nowCollapsed);
			_applyCollapseState(body, chevronEl, nowCollapsed);

			// Notify charts inside the body to resize after expanding. Shared afterLayout double-rAF
			// (item 5) instead of a magic setTimeout(50) racing layout — the body just became visible,
			// so its charts measured 0 until now.
			if (!nowCollapsed) {
				afterLayout(() => {
					body.querySelectorAll("canvas").forEach((c: HTMLCanvasElement) => {
						const chartInstance = Chart.getChart(c);
						if (chartInstance) chartInstance.resize();
					});
				});
			}
		});
	});
}

