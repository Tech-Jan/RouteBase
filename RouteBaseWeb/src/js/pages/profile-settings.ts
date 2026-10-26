import { readData } from '../core/data-attrs';
import { clearAllDevicePrefs } from '../core/device-prefs';

// profile-settings.ts — Profile Settings page

document.addEventListener("DOMContentLoaded", () => {
	// "Reset this device's layout" — the DEVICE half of the settings model, which the form above cannot
	// touch because it never reaches the server. Deliberately a separate button from "Reset to Defaults":
	// they clear different tiers, and one control doing both would hide the distinction the whole split
	// exists to make. It is also the only caller of clearAllDevicePrefs(), which is what stops the
	// enumeration from being dead code nobody would notice breaking.
	const resetDevice = document.getElementById("resetDevicePrefs");
	const resetDeviceResult = document.getElementById("resetDevicePrefsResult");
	resetDevice?.addEventListener("click", () => {
		if (!window.confirm("Forget this device's layout — panel sizes, collapsed cards, column widths and order?")) return;
		const removed = clearAllDevicePrefs();
		if (resetDeviceResult) {
			resetDeviceResult.textContent = removed === 0
				? "Nothing stored on this device."
				: `Cleared ${removed} saved layout ${removed === 1 ? "value" : "values"}. Reload to see the defaults.`;
		}
	});

	// Range sliders: sync value to adjacent <output> on drag.
	// Handles the special case of data-zero-label (e.g. FogDistance shows "Disabled" at 0).
	document.querySelectorAll<HTMLInputElement>('input[type="range"]').forEach((slider) => {
		slider.addEventListener("input", function (this: HTMLInputElement) {
			const output = this.nextElementSibling as HTMLOutputElement | null;
			if (output?.tagName === "OUTPUT") {
				const zeroLabel = readData(this, 'zeroLabel');
				output.value = (zeroLabel && this.value === "0") ? zeroLabel : this.value;
			}
		});
	});

	// Flash notice dismiss
	document.querySelectorAll<HTMLButtonElement>("[data-dismiss-notice]").forEach((btn) => {
		btn.addEventListener("click", () => {
			btn.closest(".settings-notice")?.remove();
		});
	});
});

export {};
