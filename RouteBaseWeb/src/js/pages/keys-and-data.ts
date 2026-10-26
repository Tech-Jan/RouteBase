// keys-and-data.ts — API Keys & Database page JS

import { asyncCallback } from '../core/async-listener';
import { must } from '../core/invariant';
import { readData, requireData } from '../core/data-attrs';
import { startImportPolling } from "../core/op-polling-bridge";
import { escapeHtml } from "../core/html";
import { pollUntil } from "../core/poll-until";
import { recomputeForcedTargets, type CheckboxState, type ForcingSource } from "./reset-dialog-forces";
import type {
	ApiKeyGenerateResponse, ApiKeyStatusResponse, ApiError, StravaAuthUrlResponse, StravaStatusResponse,
} from "../generated/contracts";
import type { ProgressStatus } from "../core/queue-progress-contract";

// StravaAuthUrlResponse / StravaStatusResponse come generated from Models/StravaContracts.cs.

// ── Generic dialog open / close ───────────────────────────────────────────
document.querySelectorAll<HTMLElement>("[data-open-dialog]").forEach((trigger) => {
	trigger.addEventListener("click", () => {
		const id = requireData(trigger, 'openDialog'); // [data-open-dialog] selector guarantees it
		(document.getElementById(id) as HTMLDialogElement | null)?.showModal();
	});
});

document.querySelectorAll<HTMLElement>("[data-close-dialog]").forEach((trigger) => {
	trigger.addEventListener("click", () => {
		const id = requireData(trigger, 'closeDialog'); // [data-close-dialog] selector guarantees it
		(document.getElementById(id) as HTMLDialogElement | null)?.close();
	});
});

// ── Flash notice dismiss ──────────────────────────────────────────────────
document.querySelectorAll<HTMLButtonElement>("[data-dismiss-notice]").forEach((btn) => {
	btn.addEventListener("click", () => {
		btn.closest(".sync-notice")?.remove();
	});
});

// ── Strava credentials form ───────────────────────────────────────────────
// Intercepts submit: saves credentials via API, then opens Strava OAuth in a
// new tab. The current page polls /api/strava/status and reloads on success.
document
	.querySelector<HTMLFormElement>("#stravaCredentialsDialog form")
	?.addEventListener("submit", asyncCallback('strava connect', async (e: Event) => {
		e.preventDefault();

		const btn = document.getElementById("btnSaveConnect") as HTMLButtonElement | null;
		const clientId = (document.getElementById("clientId") as HTMLInputElement | null)?.value?.trim();
		const clientSecret = (document.getElementById("clientSecret") as HTMLInputElement | null)?.value?.trim();

		if (!clientId || !clientSecret || !btn) return;

		btn.disabled = true;
		btn.innerHTML = '<span class="sync-spinner"></span>Connecting...';

		try {
			const response = await fetch("/api/strava/auth-url", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ clientId, clientSecret }),
			});

			if (!response.ok) {
				const err: Partial<ApiError> = await response.json().catch(() => ({}));
				throw new Error(err.message || "Failed to save credentials");
			}

			const body: StravaAuthUrlResponse = await response.json();
			const { authUrl } = body;

			(document.getElementById("stravaCredentialsDialog") as HTMLDialogElement | null)?.close();

			const stravaTab: Window | null = window.open(authUrl, "_blank");
			_showWaitingBanner(stravaTab);
			_pollUntilAuthenticated(stravaTab);
		} catch (error: unknown) {
			console.error("Strava connect error:", error);
			btn.disabled = false;
			btn.innerHTML = "Save and Connect";
			const msg = error instanceof Error ? error.message : String(error);
			alert("Error connecting to Strava: " + msg);
		}
	}));

function _showWaitingBanner(stravaTab: Window | null): void {
	document.getElementById("stravaWaitBanner")?.remove();

	const banner = document.createElement("div");
	banner.id = "stravaWaitBanner";
	banner.className = "sync-wait-banner";
	// eslint-disable-next-line no-unsanitized/property -- static literals only (the ternary's branches are both literals)
	banner.innerHTML =
		'<div class="sync-wait-banner-text">' +
		"<strong>Waiting for Strava authorization…</strong> " +
		"Complete the login in the Strava tab that just opened. " +
		"This page will refresh automatically once you’re connected." +
		(stravaTab
			? ' <a href="#" class="sync-wait-banner-link" id="stravaTabFocus">Switch to Strava tab</a>'
			: "") +
		"</div>" +
		'<button class="sync-wait-banner-close" id="stravaWaitClose" aria-label="Dismiss">×</button>';

	const scroll = document.querySelector<HTMLElement>(".cockpit-scroll");
	const firstSection = scroll?.querySelector<HTMLElement>(".sync-section");
	if (firstSection && scroll) {
		scroll.insertBefore(banner, firstSection);
	} else {
		(scroll ?? document.body).prepend(banner);
	}

	document.getElementById("stravaTabFocus")?.addEventListener("click", (e: Event) => {
		e.preventDefault();
		stravaTab?.focus();
	});

	document.getElementById("stravaWaitClose")?.addEventListener("click", () => {
		banner.remove();
	});
}

function _pollUntilAuthenticated(stravaTab: Window | null): void {
	void pollUntil(async () => {
		const res = await fetch("/api/strava/status");
		if (!res.ok) return false;
		const data: StravaStatusResponse = await res.json();
		if (!data.isAuthenticated) return false;
		try { stravaTab?.close(); } catch (_e) { /* tab may already be closed */ }
		window.location.reload();
		return true;
	}, {
		intervalMs: 1000,
		maxTicks: 120, // 2 minutes
		// Behavior frozen: give-up is the silent banner remove (no SO-05 message here).
		onTimeout: () => document.getElementById("stravaWaitBanner")?.remove(),
	});
}

// ── Import button ─────────────────────────────────────────────────────────
document
	.getElementById("btnImport")
	?.addEventListener("click", asyncCallback('strava import', async function (this: HTMLButtonElement) {
		const period = (document.getElementById("importPeriod") as HTMLSelectElement).value;
		const progress  = document.getElementById("importProgress") as HTMLElement;
		const progressBar  = document.getElementById("progressBar") as HTMLElement;
		const progressText = document.getElementById("progressText") as HTMLElement;
		const results   = document.getElementById("importResults") as HTMLElement;

		this.disabled = true;
		progress.style.display = "flex";
		results.innerHTML = "";
		progressBar.style.width = "10%";
		progressBar.classList.remove("sync-progress-bar--error");
		progressText.textContent = "Starting background import...";

		try {
			let afterDate: Date | null = null;
			if (period !== "all") {
				const days = parseInt(period, 10);
				afterDate = new Date();
				afterDate.setDate(afterDate.getDate() - days);
			}

			const response = await fetch("/api/strava/import-all", {
				method: "POST",
				headers: { "Content-Type": "application/json" },
				body: JSON.stringify({ after: afterDate ? afterDate.toISOString() : null }),
			});

			if (response.status === 409) {
				progressText.textContent = "Import is already running. Check the progress toast.";
				progressBar.style.width = "100%";
				startImportPolling();
				this.disabled = false;
				return;
			}

			if (!response.ok) throw new Error("Failed to start import: HTTP " + response.status);

			progressText.textContent =
				"Import running in background… you can safely leave this page.";
			progressBar.style.width = "0%";

			startImportPolling();
			_pollImportAndShowResult(this, progress, progressBar, progressText, results);
		} catch (error: unknown) {
			console.error("Import error:", error);
			const msg = error instanceof Error ? error.message : String(error);
			progressText.textContent = "Error starting import: " + msg;
			progressBar.classList.add("sync-progress-bar--error");
			this.disabled = false;
		}
	}));

// Exported for unit testing (SO-01 escaping of the Failed error sink).
export function _pollImportAndShowResult(
	btn: HTMLButtonElement,
	progressDiv: HTMLElement,
	progressBar: HTMLElement,
	progressText: HTMLElement,
	resultsDiv: HTMLElement,
): void {
	void pollUntil(async () => {
		const res = await fetch("/api/queue/progress");
		if (!res.ok) return false;
		const status: ProgressStatus = await res.json();

		// The endpoint reports the user's most-recent op — which can be a DIFFERENT op queued around
		// the Strava import (heatmap/analysis snapshots carry no import counters; "idle" carries
		// nothing). Only a Strava "import" snapshot may drive this banner; anything else keeps polling
		// (the import is still queued, or the timeout path's toast pointer takes over — SO-05).
		// Narrow on the `$type` shape AND the `operationType` label (D30): a FILE import shares the
		// import SHAPE ($type "import") but a different LABEL ("trackImport") — not this banner's op.
		if (status.$type !== "import" || status.operationType !== "import") {
			if (status.isRunning)
				progressText.textContent = "Waiting for queued operations to finish…";
			return false;
		}

		// While the background job is running, mirror its live progress next
		// to the button (the count otherwise only shows in the corner toast).
		if (status.isRunning) {
			// status is the import branch here (narrowed on $type above) — these counters are always present.
			const total = status.total;
			if (total > 0) {
				const current = status.current;
				progressBar.style.width = Math.round((current / total) * 100) + "%";
				progressText.textContent =
					`Importing ${current} / ${total} — fetching from Strava and saving to ` +
					`the database. This runs in the background; you can safely leave this page.`;
			} else {
				// Pre-count phase: the activity list is still being fetched.
				progressText.textContent =
					"Fetching activity list from Strava… This runs in the background; " +
					"you can safely leave this page.";
			}
			return false;
		}

		btn.disabled = false;
		progressDiv.style.display = "none";

		if (status.phase === "Failed") {
			resultsDiv.innerHTML =
				'<div class="sync-results sync-results--error">' +
				"<p>Import failed</p>" +
				"<ul><li>" + escapeHtml(status.error || "Unknown error") + "</li></ul>" +
				"</div>";
			fetch("/api/queue/acknowledge", { method: "POST" }).catch(() => {});
		} else if (status.phase === "Completed") {
			const imported = status.imported;
			const skipped  = status.skipped;
			const failed   = status.failed;
			// eslint-disable-next-line no-unsanitized/property -- imported/skipped/failed are numeric (import branch, always present)
			resultsDiv.innerHTML =
				'<div class="sync-results sync-results--success">' +
				"<p>Import complete!</p>" +
				"<ul>" +
				"<li><strong>" + imported + "</strong> activities imported</li>" +
				"<li><strong>" + skipped  + "</strong> skipped (already exist)</li>" +
				(failed > 0 ? "<li><strong>" + failed + "</strong> failed</li>" : "") +
				"</ul></div>";
		}
		return true;
	}, {
		intervalMs: 1000,
		maxTicks: 600, // 10 minutes
		onTimeout: () => {
			// SO-05: point the user at the still-accurate corner toast instead of stopping silently.
			progressText.textContent = "Still running after 10 minutes — check the toast in the corner for the latest status.";
			btn.disabled = false;
		},
	});
}

// ── Reset database dialog ─────────────────────────────────────────────────
(() => {
	const dialog       = document.getElementById("resetDatabaseDialog") as HTMLDialogElement | null;
	const confirmInput = document.getElementById("confirmationText")    as HTMLInputElement  | null;
	const btnConfirm   = document.getElementById("btnConfirmReset")     as HTMLButtonElement | null;
	const depNotice    = document.getElementById("depNotice")           as HTMLElement       | null;
	const depText      = document.getElementById("depNoticeText")       as HTMLElement       | null;
	const form         = document.getElementById("resetDatabaseForm")   as HTMLFormElement   | null;

	// The five children live inside the dialog markup — if any is missing that is markup drift,
	// and wiring half a destructive dialog is worse than wiring none of it (G3: guard, not `!`).
	if (!dialog || !confirmInput || !btnConfirm || !depNotice || !depText || !form) return;

	// Recompute every group master's checked/indeterminate state from its children.
	const refreshMasters = (): void => {
		dialog.querySelectorAll<HTMLInputElement>(".group-master").forEach((master) => {
			const children   = dialog.querySelectorAll<HTMLInputElement>(`.child-check[data-group="${readData(master, 'group')}"]`);
			const anyChecked = Array.from(children).some((x) => x.checked);
			const allChecked = Array.from(children).every((x) => x.checked);
			master.indeterminate = anyChecked && !allChecked;
			master.checked       = allChecked;
		});
	};

	dialog.querySelectorAll<HTMLInputElement>(".group-master").forEach((master) => {
		const group    = readData(master, 'group');
		const children = dialog.querySelectorAll<HTMLInputElement>(`.child-check[data-group="${group}"]`);

		master.addEventListener("change", () => {
			children.forEach((c) => { c.checked = master.checked; c.disabled = !master.checked; });
			updateDependencies();
			updateConfirmButton();
		});

		children.forEach((c) => c.addEventListener("change", refreshMasters));
	});

	// Quick-select presets: clear every box, then check only the preset's group.
	dialog.querySelectorAll<HTMLButtonElement>("[data-quick-select]").forEach((btn) => {
		btn.addEventListener("click", () => {
			const preset = readData(btn, 'quickSelect');
			dialog.querySelectorAll<HTMLInputElement>(".child-check").forEach((c) => {
				c.disabled = false;
				c.checked  = preset === "photos" && c.id === "chkPhotos";
			});
			refreshMasters();
			updateDependencies();
			updateConfirmButton();
		});
	});

	// DOM adapter around the pure recompute (reset-dialog-forces.ts): read sources + targets,
	// apply the returned states, render the notices.
	const updateDependencies = (): void => {
		const sources: ForcingSource[] = Array.from(
			dialog.querySelectorAll<HTMLInputElement>(".child-check[data-forces]"),
		).map((el) => ({
			id: el.id,
			checked: el.checked,
			// [data-forces] is in the selector, so the attribute exists — requireData states that.
			forces: requireData(el, 'forces').split(",").map((s) => s.trim()),
		}));

		const targets = new Map<string, HTMLInputElement>();
		sources.forEach((src) => src.forces.forEach((id) => {
			const el = document.getElementById(id) as HTMLInputElement | null;
			if (el) targets.set(id, el);
		}));
		const current: Record<string, CheckboxState> = {};
		targets.forEach((el, id) => { current[id] = { checked: el.checked, disabled: el.disabled }; });

		const { states, notices } = recomputeForcedTargets(sources, current);
		targets.forEach((el, id) => {
			const st = must(states[id], 'recomputeForcedTargets covers every target id');
			el.checked  = st.checked;
			el.disabled = st.disabled;
		});

		const labelOf = (id: string): string =>
			(document.getElementById(id) as HTMLInputElement | null)
				?.labels?.[0]?.querySelector("strong")?.textContent ?? id;
		if (notices.length > 0) {
			depText.textContent = notices
				.map((n) => `"${labelOf(n.targetId)}" is required because "${labelOf(n.sourceId)}" is selected.`)
				.join(" ");
			depNotice.classList.remove("hidden");
		} else {
			depNotice.classList.add("hidden");
		}
	};

	dialog.querySelectorAll<HTMLInputElement>(".child-check").forEach((chk) => {
		chk.addEventListener("change", () => {
			updateDependencies();
			updateConfirmButton();
		});
	});

	const updateConfirmButton = (): void => {
		const anyChecked = Array.from(dialog.querySelectorAll<HTMLInputElement>(".child-check")).some((c) => c.checked);
		const confirmed  = confirmInput.value.trim() === "DELETE";
		btnConfirm.disabled = !(anyChecked && confirmed);
	};

	confirmInput.addEventListener("input", updateConfirmButton);

	// Reset all state when the dialog is closed (Escape or Cancel)
	dialog.addEventListener("close", () => {
		confirmInput.value = "";
		btnConfirm.disabled = true;
		depNotice.classList.add("hidden");
		dialog.querySelectorAll<HTMLInputElement>(".child-check").forEach((c) => {
			c.disabled = false;
			c.checked  = c.id !== "chkSegments";
		});
		dialog.querySelectorAll<HTMLInputElement>(".group-master").forEach((m) => {
			m.checked       = true;
			m.indeterminate = false;
		});
		const segMaster = document.getElementById("grpSegments") as HTMLInputElement | null;
		if (segMaster) { segMaster.checked = false; segMaster.indeterminate = true; }
		updateDependencies();
	});

	form.addEventListener("submit", () => {
		btnConfirm.disabled = true;
		btnConfirm.innerHTML = '<span class="sync-spinner"></span>Deleting...';
	});

	// Init
	updateDependencies();
	const segMaster = document.getElementById("grpSegments") as HTMLInputElement | null;
	if (segMaster) { segMaster.checked = false; segMaster.indeterminate = true; }
})();

// ── Mobile API Key ────────────────────────────────────────────────────────

// ApiKeyStatusResponse / ApiKeyGenerateResponse come generated from Models/ApiKeyContracts.cs.

void (async function loadApiKeyStatus() {
	const res = await fetch("/api/apikeys/status");
	if (!res.ok) return;
	const data: ApiKeyStatusResponse = await res.json();
	renderApiKeyStatus(data.hasKey, data.createdAt);
})();

function renderApiKeyStatus(hasKey: boolean, createdAt: string | null): void {
	const statusEl  = document.getElementById("apiKeyStatus");
	const revokeBtn = document.getElementById("revokeKeyBtn");
	if (!statusEl) return;

	if (hasKey) {
		const date = createdAt ? new Date(createdAt).toLocaleDateString() : "unknown date";
		statusEl.innerHTML = `<span class="sync-status sync-status--connected">API key active (created ${escapeHtml(date)})</span>`;
		revokeBtn?.classList.remove("hidden");
	} else {
		statusEl.innerHTML = '<span style="color:var(--fg-3)">No API key configured</span>';
		revokeBtn?.classList.add("hidden");
	}
}

document.getElementById("generateKeyBtn")?.addEventListener("click", asyncCallback('generate api key', async () => {
	if (!confirm("Generate a new API key? Any existing key will be revoked immediately.")) return;
	// try/catch: a network failure otherwise dies as an unhandled rejection with zero feedback —
	// on an endpoint that revokes-then-creates, the user must know the outcome is unknown.
	let res: Response | null = null;
	try {
		res = await fetch("/api/apikeys/generate", { method: "POST" });
	} catch { /* network failure — handled below */ }
	if (!res?.ok) { alert("Failed to generate key — the old key may already be revoked. Check the status after reloading."); return; }
	const data: ApiKeyGenerateResponse = await res.json();
	const keyText   = document.getElementById("apiKeyText");
	const keyReveal = document.getElementById("apiKeyValue");
	// Show the full setup code (origin|key), not the bare key: the Android app needs
	// both parts, so pasting only the key never works. Display must equal what's copied.
	if (keyText) keyText.textContent = window.location.origin + "|" + data.key;
	keyReveal?.classList.remove("hidden");
	renderApiKeyStatus(true, data.createdAt);
}));

document.getElementById("revokeKeyBtn")?.addEventListener("click", asyncCallback('revoke api key', async () => {
	if (!confirm("Revoke the API key? The Android app will stop working until a new key is generated.")) return;
	let res: Response | null = null;
	try {
		res = await fetch("/api/apikeys", { method: "DELETE" });
	} catch { /* network failure — handled below */ }
	if (!res?.ok) { alert("Failed to revoke key."); return; }
	document.getElementById("apiKeyValue")?.classList.add("hidden");
	renderApiKeyStatus(false, null);
}));

document.getElementById("copyKeyBtn")?.addEventListener("click", () => {
	// The field already holds the full setup code — copy exactly what the user sees.
	const setupCode = document.getElementById("apiKeyText")?.textContent ?? "";
	const btn       = document.getElementById("copyKeyBtn") as HTMLButtonElement | null;

	navigator.clipboard.writeText(setupCode).then(() => {
		if (!btn) return;
		const orig = btn.textContent;
		btn.textContent = "Copied!";
		setTimeout(() => { btn.textContent = orig; }, 2000);
	}).catch(() => {
		// Clipboard permission denied — say so instead of an unhandled rejection + silent button.
		if (btn) btn.textContent = "Copy blocked — select the key manually";
	});
});

export {};
