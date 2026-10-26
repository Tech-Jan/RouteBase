/**
 * Activity Details - Map Controls Module
 * Handles color modes and chart zoom.
 * Style switching, style buttons, and size controls are handled by MapShell.
 */

import { readData } from '../core/data-attrs';
import { mapDispatcher, setCurrentColorMode } from '../core/map-dispatcher';
import { resetChartZoomAll } from './activity-details';

export function setupMapControls(): void {
	// Get DOM elements
	const colorNoneBtn     = document.getElementById("colorNoneBtn");
	const colorSpeedBtn    = document.getElementById("colorSpeedBtn");
	const colorHrBtn       = document.getElementById("colorHrBtn");
	const colorElevBtn     = document.getElementById("colorElevBtn");
	const colorCadenceBtn  = document.getElementById("colorCadenceBtn");

	const resetElevationZoomBtn = document.getElementById("resetElevationZoom");
	const resetSpeedZoomBtn     = document.getElementById("resetSpeedZoom");
	const resetHrZoomBtn        = document.getElementById("resetHrZoom");

	function setActiveColorButton(activeBtn: HTMLElement | null): void {
		[colorNoneBtn, colorSpeedBtn, colorHrBtn, colorElevBtn, colorCadenceBtn].forEach((btn) => {
			btn?.classList.remove("active");
		});
		activeBtn?.classList.add("active");
	}

	// Color mode buttons — track the current color mode (map-dispatcher state) so engine
	// switches can re-apply the correct mode in MapShell callbacks.
	colorNoneBtn?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		setCurrentColorMode('none');
		mapDispatcher.updateTrackColors("none");
		setActiveColorButton(colorNoneBtn);
	});

	colorSpeedBtn?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		setCurrentColorMode('speed');
		mapDispatcher.updateTrackColors("speed");
		setActiveColorButton(colorSpeedBtn);
	});

	colorHrBtn?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		setCurrentColorMode('heartrate');
		mapDispatcher.updateTrackColors("heartrate");
		setActiveColorButton(colorHrBtn);
	});

	colorElevBtn?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		setCurrentColorMode('elevation');
		mapDispatcher.updateTrackColors("elevation");
		setActiveColorButton(colorElevBtn);
	});

	colorCadenceBtn?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		setCurrentColorMode('cadence');
		mapDispatcher.updateTrackColors("cadence");
		setActiveColorButton(colorCadenceBtn);
	});

	// Chart zoom reset buttons — all three reset all charts (cancels any in-flight
	// gesture preview and re-decimates to full extent, see resetChartZoomAll)
	function _resetAllChartZoom(e: Event): void {
		e.preventDefault();
		resetChartZoomAll();
	}
	resetElevationZoomBtn?.addEventListener("click", _resetAllChartZoom);
	resetSpeedZoomBtn?.addEventListener("click", _resetAllChartZoom);
	resetHrZoomBtn?.addEventListener("click", _resetAllChartZoom);

	setupToolbarDropdowns();
}

/**
 * Wires the map-toolbar dropdowns (.det-dd). The option buttons inside keep their original IDs and
 * click handlers (color mode here, map style + size in MapShell); this only manages the dropdown
 * shell: keeping each summary's value label in sync with the active option, closing single-select
 * pickers after a choice, and closing any open dropdown on an outside click (native <details> does
 * neither on its own).
 */
function setupToolbarDropdowns(): void {
	const dropdowns = Array.from(document.querySelectorAll<HTMLDetailsElement>(".det-dd"));

	dropdowns.forEach((dd) => {
		const isPicker = readData(dd, 'picker') === "true"; // single-select (color/style) vs action menu (size)
		const valueEl = dd.querySelector<HTMLElement>(".det-dd-value");

		// Mirror the active option's label into the summary. The active class is toggled by the option
		// click handlers and (for style) set programmatically on load / engine switch — a MutationObserver
		// keeps the label correct in every case.
		const syncValue = (): void => {
			if (!valueEl) return;
			const active = dd.querySelector<HTMLElement>(".det-map-btn.active");
			if (active?.textContent) valueEl.textContent = active.textContent.trim();
		};

		if (isPicker) {
			syncValue();
			new MutationObserver(syncValue).observe(dd, {
				subtree: true, attributes: true, attributeFilter: ["class"],
			});
			// Close the picker once a choice is made. Delegated because the style switcher buttons are
			// rendered by MapShell (createMapShell) — a per-button listener bound here could miss them.
			// Defer so the option's own handler sets .active first.
			dd.addEventListener("click", (e) => {
				if ((e.target as HTMLElement).closest(".det-map-btn")) {
					requestAnimationFrame(() => { dd.open = false; });
				}
			});
		}
	});

	// Outside-click closes any open dropdown (size stays open across repeated grow/shrink clicks inside it).
	document.addEventListener("click", (e: Event) => {
		dropdowns.forEach((dd) => {
			if (dd.open && !dd.contains(e.target as Node)) dd.open = false;
		});
	});
}
