// import.ts — unified Import page: drop activity files + photos, route each by type. Activity files
// (.gpx/.tcx/.fit) go through the activity-import batch; images through the photo-import batch.
// With a target activity (?trackId=, "Add photos" from Details) images import straight to it (skip matching).

import { asyncCallback } from '../core/async-listener';
import { must } from '../core/invariant';
import { readData } from '../core/data-attrs';
import { startTrackImportPolling } from "../core/op-polling-bridge";
import { escapeHtml } from "../core/html";
import { formatBytes } from "../core/format";
import { pollUntil } from "../core/poll-until";
import { postFormWithProgress, type UploadResponse } from "../core/xhr-upload";
import { chunkBySize } from "./import-chunking";
import { isSkip, skipBadge } from "./import-skip";
import type { ProgressStatus } from "../core/queue-progress-contract";

type Kind = "activity" | "photo";
type Status = "pending" | "queued" | "importing" | "imported" | "skipped" | "in-trash" | "in-work" | "duplicate" | "ambiguous" | "unmatched" | "failed";

interface Entry {
	file: File;
	kind: Kind;
	li: HTMLLIElement;
	status: Status;
}

/** Generated track-import begin response; Photos' richer PhotoBeginBatchResponse also carries
 * batchId, so this module (which only reads batchId) uses the one type for both begin calls.
 * The photo-import polling shapes come from GET /api/photos/import-status (PhotoImportSnapshot →
 * PhotoFileResult → PhotoCandidateDto), an annotated JSON endpoint — generated, not the polymorphic
 * queue-progress union (that stays hand-modeled as ProgressStatus, D13). TODO(openapi-review): D17. */
import type { BeginBatchResponse, ApiError, PhotoImportSnapshot, PhotoFileResult, PolicyRejectedBody } from '../generated/contracts';

/** This page reads only the run flag + per-file results — narrow view (D4) tied to the generated snapshot. */
type PhotoImportStatus = Pick<PhotoImportSnapshot, 'isRunning' | 'results'>;
// The activity-import queue-progress shape (ProgressStatus) is imported from ../core/queue-progress-contract.

const ACTIVITY_EXT = [".gpx", ".tcx", ".fit"];
const IMAGE_EXT = [".jpg", ".jpeg", ".png", ".webp"];
// A chunk is capped by BOTH count and bytes: 40 phone photos in one request exceeded ASP.NET's
// multipart form limit, which kills the connection and surfaces as a 502 behind the proxy.
// Bytes are capped low because the reverse proxy aborts any request that takes over 60 s: a chunk
// must fit in that window on a slow uplink (~4 MB ≈ 30 s at 1 Mbit/s), or it 502s halfway through.
const CHUNK_MAX_FILES = 40;
const CHUNK_MAX_BYTES = 4 * 1024 * 1024;
// A 502/503/504 mid-upload is the proxy, not a verdict on the files — retry the chunk before
// failing the batch. Gateway errors only: a 4xx is a real rejection and retrying it just stalls.
const CHUNK_RETRIES = 3;
const RETRY_BACKOFF_MS = 2000;
const GATEWAY_STATUS = [502, 503, 504];

const root          = document.getElementById("importRoot") as HTMLElement;
const trackIdAttr = root ? readData(root, 'trackId') : null;
const targetTrackId = trackIdAttr ? Number(trackIdAttr) : null;

const dropZone   = document.getElementById("dropZone") as HTMLDivElement;
const fileInput  = document.getElementById("fileInput") as HTMLInputElement;
const fileQueue  = document.getElementById("fileQueue") as HTMLDivElement;
const fileList   = document.getElementById("fileList") as HTMLUListElement;
const btnUpload  = document.getElementById("btnUploadAll") as HTMLButtonElement;
const btnClear   = document.getElementById("btnClear") as HTMLButtonElement;
const summary    = document.getElementById("importSummary") as HTMLDivElement;
const useFilename = document.getElementById("useFilenameAsId") as HTMLInputElement | null;
const previewSize = document.getElementById("previewSize") as HTMLSelectElement | null;
const progressBox  = document.getElementById("uploadProgress") as HTMLDivElement | null;
const progressText = document.getElementById("uploadProgressText") as HTMLElement | null;
const progressFill = document.getElementById("uploadProgressFill") as HTMLElement | null;

let queue: Entry[] = [];
let openBatchId: string | null = null;
let openBatchKind: Kind | null = null;
let uploadedBytes = 0;
let totalBytes = 0;

// ── Drag & drop ────────────────────────────────────────────────────
dropZone.addEventListener("dragover", (e) => { e.preventDefault(); dropZone.classList.add("gpx-dropzone--over"); });
dropZone.addEventListener("dragleave", () => dropZone.classList.remove("gpx-dropzone--over"));
dropZone.addEventListener("drop", (e) => {
	e.preventDefault();
	dropZone.classList.remove("gpx-dropzone--over");
	if (e.dataTransfer) addFiles(Array.from(e.dataTransfer.files));
});
dropZone.addEventListener("click", (e) => {
	if ((e.target as HTMLElement).closest("button, input")) return;
	fileInput.click();
});
document.getElementById("btnBrowse")?.addEventListener("click", () => fileInput.click());
// ?? []: a file input's .files is null only for non-file inputs per the DOM types; empty = no-op.
fileInput.addEventListener("change", () => { addFiles(Array.from(fileInput.files ?? [])); fileInput.value = ""; });

function extOf(name: string): string {
	const dot = name.lastIndexOf(".");
	return dot >= 0 ? name.slice(dot).toLowerCase() : "";
}

function kindOf(name: string): Kind | null {
	const lower = name.toLowerCase();
	if (ACTIVITY_EXT.some((e) => lower.endsWith(e))) return "activity";
	if (IMAGE_EXT.some((e) => lower.endsWith(e))) return "photo";
	return null;
}

function addFiles(files: File[]): void {
	for (const file of files) {
		const kind = kindOf(file.name);
		if (!kind) continue;
		// In "add photos to this activity" mode only images make sense.
		if (targetTrackId !== null && kind !== "photo") continue;

		const li = document.createElement("li");
		li.className = "gpx-file-item";
		const typeBadge = kind === "activity" ? extOf(file.name).slice(1).toUpperCase() : "Photo";
		// eslint-disable-next-line no-unsanitized/property -- file.name and typeBadge escapeHtml'd (typeBadge is also allowlisted: kindOf's endsWith gate ⇒ .gpx/.tcx/.fit, or "Photo")
		li.innerHTML =
			`<span class="gpx-file-name"><i class="bi ${kind === "activity" ? "bi-file-earmark-text" : "bi-image"}" aria-hidden="true"></i><span>${escapeHtml(file.name)}</span></span>` +
			`<span class="import-type-badge">${escapeHtml(typeBadge)}</span>` +
			`<span class="gpx-badge gpx-badge--pending status-badge">Pending</span>`;
		fileList.appendChild(li);
		queue.push({ file, kind, li, status: "pending" });
	}
	if (queue.length) { fileQueue.style.display = ""; summary.style.display = "none"; }
}

btnClear.addEventListener("click", () => {
	queue = [];
	fileList.innerHTML = "";
	fileQueue.style.display = "none";
	summary.style.display = "none";
	hideProgress();
	const results = document.getElementById("importResults");
	if (results) results.innerHTML = "";
});

btnUpload.addEventListener("click", asyncCallback('upload all', () => uploadAll()));

// ── Upload orchestration (serialized: activities then photos, single import slot) ─────────────
async function uploadAll(): Promise<void> {
	const pending = queue.filter((e) => e.status === "pending");
	if (!pending.length) return;
	btnUpload.disabled = true;
	btnClear.disabled = true;
	startProgress(sumBytes(pending));

	try {
		const activities = pending.filter((e) => e.kind === "activity");
		const photos = pending.filter((e) => e.kind === "photo");
		if (activities.length) await runActivityImport(activities);
		if (photos.length) await runPhotoImport(photos);
	} finally {
		btnUpload.disabled = false;
		btnClear.disabled = false;
	}
}

// ── Upload progress (staging bytes; processing progress is the per-file badges) ────────────────
function sumBytes(entries: Entry[]): number {
	return entries.reduce((n, e) => n + e.file.size, 0);
}

function chunkEntries(entries: Entry[]): Entry[][] {
	return chunkBySize(entries, (e) => e.file.size, CHUNK_MAX_FILES, CHUNK_MAX_BYTES);
}

function startProgress(bytes: number): void {
	uploadedBytes = 0;
	totalBytes = bytes;
	if (progressBox) progressBox.style.display = "";
	renderProgress();
}

function setUploaded(bytes: number): void {
	uploadedBytes = Math.min(bytes, totalBytes);
	renderProgress();
}

function renderProgress(): void {
	const pct = totalBytes > 0 ? Math.round((uploadedBytes / totalBytes) * 100) : 0;
	if (progressText) {
		progressText.textContent = uploadedBytes >= totalBytes && totalBytes > 0
			? `${formatBytes(totalBytes)} uploaded — processing…`
			: `${formatBytes(uploadedBytes)} / ${formatBytes(totalBytes)} uploaded (${pct}%)`;
	}
	if (progressFill) progressFill.style.width = `${pct}%`;
}

function hideProgress(): void {
	if (progressBox) progressBox.style.display = "none";
	uploadedBytes = 0;
	totalBytes = 0;
}

// ── Generic batch staging (begin → chunk → commit) ────────────────────────────
async function stageAndCommit(base: Kind, entries: Entry[], beginFields: Record<string, string>): Promise<string | null> {
	const apiBase = base === "activity" ? "/api/import" : "/api/photos";
	// begin
	let batchId: string;
	try {
		const fd = new FormData();
		for (const [k, v] of Object.entries(beginFields)) fd.append(k, v);
		const resp = await fetch(`${apiBase}/batch/begin`, { method: "POST", body: fd });
		// Parse defensively (FE-11): a gateway error page (HTML 502 mid-deploy) is not JSON, and parsing
		// it before the ok-check turned a plain server status into "Network error". Same shape as the
		// commit/chunk paths' `.catch(() => ({}))`.
		const data = await resp.json().catch(() => ({})) as BeginBatchResponse & Partial<ApiError>;
		if (!resp.ok) { showError(data.message ?? resp.statusText); return null; }
		// C# declares BatchId nullable, but the 200 path always sets it — coalesce for the type only.
		batchId = data.batchId ?? "";
	} catch { showError("Network error — could not start the import."); return null; }

	openBatchId = batchId;
	openBatchKind = base;

	// chunked staging
	for (const chunk of chunkEntries(entries)) {
		const chunkBytes = sumBytes(chunk);
		const base = uploadedBytes;
		for (const e of chunk) setBadge(e.li, "Uploading…", "importing");
		try {
			const resp = await postChunk(`${apiBase}/batch/${batchId}/files`, chunk, base, chunkBytes);
			if (!resp.ok) {
				const d = (resp.body ?? {}) as Partial<PolicyRejectedBody>;
				// SO-02: the validator returns files[] naming the offending file(s) — badge those
				// entries "Rejected" with the reason as tooltip and mark them terminal ("failed") so the
				// caller's failPending sweep skips them (its guard only touches pending/queued entries).
				const bad: string[] = Array.isArray(d.files) ? d.files.filter((x): x is string => typeof x === "string") : [];
				if (bad.length) {
					const badSet = new Set(bad);
					for (const e of chunk) if (badSet.has(e.file.name)) { setBadge(e.li, "Rejected", "failed", d.message ?? undefined); e.status = "failed"; }
				}
				showError(bad.length
					? `${d.message ?? "Upload failed."} (${bad.length === 1 ? bad[0] : `${bad.length} files`})`
					: GATEWAY_STATUS.includes(resp.status)
						? uploadInterruptedMessage()
						: (d.message ?? `Upload failed (${resp.status} ${resp.statusText}).`));
				await abort(apiBase, batchId);
				return null;
			}
		} catch { showError(uploadInterruptedMessage()); await abort(apiBase, batchId); return null; }
		setUploaded(base + chunkBytes);
		for (const e of chunk) { setBadge(e.li, "Queued…", "queued"); e.status = "queued"; }
	}

	// commit
	try {
		const resp = await fetch(`${apiBase}/batch/${batchId}/commit`, { method: "POST" });
		if (!resp.ok) { const d = await resp.json().catch(() => ({})); showError(d.message ?? resp.statusText); await abort(apiBase, batchId); return null; }
	} catch { showError("Network error — could not start processing."); await abort(apiBase, batchId); return null; }

	openBatchId = null;
	openBatchKind = null;
	return batchId;
}

/**
 * POSTs one chunk, retrying gateway errors and dropped connections — the proxy cuts any request that
 * runs past 60 s, so a slow uplink can lose a chunk that would succeed on a second try. Each attempt
 * re-sends the whole chunk, so the progress counter rewinds to where the chunk started. A retry may
 * re-stage files the server already holds (it answered into a dead connection) — harmless: both import
 * paths dedupe, an activity re-import as "duplicate" and a photo by content hash.
 */
async function postChunk(url: string, chunk: Entry[], base: number, chunkBytes: number): Promise<UploadResponse> {
	let lastResp: UploadResponse | null = null;
	for (let attempt = 1; attempt <= CHUNK_RETRIES; attempt++) {
		const fd = new FormData();
		for (const e of chunk) fd.append("files", e.file);
		try {
			// Multipart framing makes loaded exceed the file bytes — clamp so the counter can't overshoot.
			const resp = await postFormWithProgress(url, fd,
				(loaded) => setUploaded(base + Math.min(loaded, chunkBytes)));
			// A non-gateway status is the server's verdict on these files — never retry it.
			if (resp.ok || !GATEWAY_STATUS.includes(resp.status)) return resp;
			lastResp = resp;
		} catch (err) {
			if (attempt === CHUNK_RETRIES) throw err;
		}
		if (attempt < CHUNK_RETRIES) {
			setUploaded(base);
			for (const e of chunk) setBadge(e.li, `Retrying… (${attempt + 1}/${CHUNK_RETRIES})`, "importing");
			await new Promise((r) => setTimeout(r, RETRY_BACKOFF_MS * attempt));
		}
	}
	// Reachable only when every attempt hit the gateway-retry branch, which stores the response.
	return must(lastResp, 'postChunk lastResp (gateway-retry loop stores every attempt)');
}

async function abort(apiBase: string, batchId: string): Promise<void> {
	openBatchId = null;
	openBatchKind = null;
	try { await fetch(`${apiBase}/batch/${batchId}/abort`, { method: "POST" }); } catch { /* best-effort */ }
}

window.addEventListener("pagehide", () => {
	if (openBatchId && openBatchKind) {
		const apiBase = openBatchKind === "activity" ? "/api/import" : "/api/photos";
		navigator.sendBeacon(`${apiBase}/batch/${openBatchId}/abort`);
		openBatchId = null;
	}
});

// ── Activity (GPX) import ─────────────────────────────────────────────────────
async function runActivityImport(entries: Entry[]): Promise<void> {
	const batchId = await stageAndCommit("activity", entries, { useFilenameAsId: String(!!useFilename?.checked) });
	if (!batchId) { failPending(entries); return; }
	startTrackImportPolling();
	const byName = new Map(entries.map((e) => [e.file.name, e]));
	await pollQueue(byName);
}

async function pollQueue(byName: Map<string, Entry>): Promise<void> {
	await pollUntil(async () => {
		const r = await fetch("/api/queue/progress");
		if (!r.ok) return false;
		const s: ProgressStatus = await r.json();
		// Only the import's OWN snapshots may drive badges or settle the poll: the endpoint reports the
		// user's most-recent op, and a different op (analysis/heatmap queued around the import) carries
		// no `results` — settling on it froze per-file badges on their last mid-run state.
		// Narrow on the `$type` import SHAPE (D30) for `results`, plus the `trackImport` LABEL (this page's op).
		if (s.$type !== "import" || s.operationType !== "trackImport") return false;
		for (const res of s.results) {
			const e = byName.get(res.fileName);
			if (!e) continue;
			if (res.outcome === "imported") { setBadge(e.li, "Imported", "imported"); e.status = "imported"; }
			else if (res.outcome === "failed") { setBadge(e.li, "Error", "failed", res.detail ?? undefined); e.status = "failed"; }
			else if (res.outcome === "importing") { setBadge(e.li, "Importing…", "importing"); e.status = "importing"; }
			else if (isSkip(res.outcome)) { setBadge(e.li, skipBadge(res.outcome), "skipped", res.detail ?? undefined); e.status = res.outcome as Status; }
		}
		if (s.isRunning) return false;
		fetch("/api/queue/acknowledge", { method: "POST" }).catch(() => {});
		return true;
	}, {
		intervalMs: 1000,
		maxTicks: 1200, // 20 minutes
		// SO-05: don't give up silently — the backend job may still be running.
		onTimeout: () => showError("Still waiting on the server after 20 minutes — check the operation toast for the latest status."),
	});
}

// ── Photo import ──────────────────────────────────────────────────────────────
async function runPhotoImport(entries: Entry[]): Promise<void> {
	const fields: Record<string, string> = { previewMaxEdge: previewSize?.value ?? "1024" };
	if (targetTrackId !== null) fields.trackId = String(targetTrackId);

	const batchId = await stageAndCommit("photo", entries, fields);
	if (!batchId) { failPending(entries); return; }
	const byName = new Map(entries.map((e) => [e.file.name, e]));
	await pollPhotoStatus(byName, batchId);
}

async function pollPhotoStatus(byName: Map<string, Entry>, batchId: string): Promise<void> {
	await pollUntil(async () => {
		const r = await fetch("/api/photos/import-status");
		if (!r.ok) return false;
		const s: PhotoImportStatus = await r.json();
		renderPhotoResults(byName, batchId, s.results);
		return !s.isRunning;
	}, {
		intervalMs: 1000,
		maxTicks: 1200, // 20 minutes
		// SO-05: surface the give-up instead of freezing the queue-list badges silently.
		onTimeout: () => showError("Still waiting on the server after 20 minutes — check the operation toast for the latest status."),
	});
}

function renderPhotoResults(byName: Map<string, Entry>, batchId: string, results: PhotoFileResult[]): void {
	const panel = document.getElementById("importResults") as HTMLDivElement;
	for (const r of results) {
		const e = byName.get(r.fileName);
		if (e) {
			const label: Record<string, [string, string]> = {
				importing: ["Importing…", "importing"], imported: ["Imported", "imported"],
				duplicate: ["Duplicate — skipped", "duplicate"], ambiguous: ["Needs a pick", "ambiguous"],
				unmatched: ["No activity match", "unmatched"], failed: ["Error", "failed"],
			};
			const [text, variant] = label[r.outcome] ?? [r.outcome, "pending"];
			setBadge(e.li, text, variant, r.error ?? undefined);
			e.status = r.outcome as Status;
		}

		// Inline resolution for ambiguous (candidate pick). Unmatched → guide to the activity's "Add photos".
		if (r.outcome === "ambiguous" && r.stagedName && r.candidates?.length) {
			renderCandidatePicker(panel, batchId, r);
		}
	}
}

function renderCandidatePicker(panel: HTMLDivElement, batchId: string, r: PhotoFileResult): void {
	// Wire guard: the picker only makes sense for a result carrying its staged name + candidates.
	const stagedName = r.stagedName;
	const candidates = r.candidates;
	if (!stagedName || !candidates) return;
	const id = `resolve-${cssId(stagedName)}`;
	if (document.getElementById(id)) return; // already rendered
	const row = document.createElement("div");
	row.className = "import-resolve";
	row.id = id;
	const btns = candidates
		.map((c) => `<button class="btn-cockpit btn-cockpit-ghost btn-cockpit-sm" data-track="${c.trackId}">` +
			`${escapeHtml(c.name)} <small>${new Date(c.startLocal).toLocaleDateString()}${c.gpsDistanceM != null ? ` · ${Math.round(c.gpsDistanceM)} m` : ""}</small></button>`)
		.join("");
	// eslint-disable-next-line no-unsanitized/property -- fileName/name escapeHtml'd; btns built above interpolate numeric trackId/distance + toLocaleDateString
	row.innerHTML = `<span class="import-resolve-name"><i class="bi bi-question-circle" aria-hidden="true"></i> ${escapeHtml(r.fileName)} — which activity?</span><div class="import-resolve-choices">${btns}</div>`;
	panel.appendChild(row);
	panel.style.display = "";

	row.querySelectorAll("button[data-track]").forEach((b) =>
		b.addEventListener("click", asyncCallback('assign photo track', async () => {
			const trackId = Number(readData(b as HTMLElement, 'track'));
			row.querySelectorAll("button").forEach((x) => ((x as HTMLButtonElement).disabled = true));
			const ok = await assign(batchId, stagedName, trackId, false);
			// eslint-disable-next-line no-unsanitized/property -- fileName escapeHtml'd in both branches; rest static literals
			row.innerHTML = ok
				? `<span class="import-resolve-name import-resolve-done"><i class="bi bi-check-circle" aria-hidden="true"></i> ${escapeHtml(r.fileName)} — assigned.</span>`
				: `<span class="import-resolve-name import-resolve-err">Could not assign ${escapeHtml(r.fileName)}.</span>`;
		})));
}

async function assign(batchId: string, stagedName: string, trackId: number, keepAnyway: boolean): Promise<boolean> {
	try {
		const resp = await fetch("/api/photos/assign", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ batchId, stagedName, trackId, keepAnyway }),
		});
		return resp.ok;
	} catch { return false; }
}

// ── Helpers ────────────────────────────────────────────────────────
function failPending(entries: Entry[]): void {
	for (const e of entries) if (e.status === "pending" || e.status === "queued") { setBadge(e.li, "Error", "failed"); e.status = "failed"; }
}

function setBadge(li: HTMLLIElement, text: string, variant: string, tooltip?: string): void {
	const badge = li.querySelector(".status-badge") as HTMLElement;
	badge.className = `gpx-badge gpx-badge--${variant} status-badge`;
	badge.textContent = text;
	if (tooltip) badge.title = tooltip; else badge.removeAttribute("title");
}

// The upload died mid-chunk and the retries are spent — the files are fine, the connection was not.
// Says what to do next, because "Upload failed" alone reads as a verdict on the files.
function uploadInterruptedMessage(): string {
	return `Upload interrupted after ${CHUNK_RETRIES} attempts — the connection dropped mid-transfer. ` +
		"Nothing was imported; check your connection and press Import All again.";
}

function showError(message: string): void {
	summary.className = "gpx-summary gpx-summary--error";
	summary.innerHTML = `<strong>Error:</strong> ${escapeHtml(message)}`;
	summary.style.display = "";
}

function cssId(s: string): string { return s.replace(/[^a-zA-Z0-9_-]/g, "_"); }

export {};
