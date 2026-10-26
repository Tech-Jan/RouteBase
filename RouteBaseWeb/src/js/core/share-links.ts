// share-links.ts — the owner's Share-link dialog, for any shareable resource.
//
// Mint / list / revoke against /api/sharelinks. The plaintext token exists exactly once, in the mint
// response: only its SHA-256 is stored, so it can never be listed or re-displayed. That is why the UI
// shows the URL once with a Copy button and thereafter offers only "revoke".
//
// Kind-agnostic: the caller passes the wire kind, so activities and segments share one dialog, one
// module and one set of element ids. The ids are deliberately unchanged from the activity-only version
// (shareActivityBtn / shareLinkDialog) — only one dialog exists per page, and renaming them would churn
// the render guards that pin them for no behavioural gain.

import { readApiErrorMessage } from "./api-error";
import { readData } from '../core/data-attrs';
import { escapeHtml } from "./html";
// Generated from Models/ShareLinkContracts.cs. The mint URL is relative ("/s/<token>") — the server
// never builds an absolute URL from a spoofable Host header.
import type { ShareLinkListResponse, ShareLinkMintResponse, ShareLinkSummaryDto } from "../generated/contracts";

const TTL_OPTIONS: ReadonlyArray<{ value: string; label: string }> = [
	{ value: "24h", label: "24 hours" },
	{ value: "7d", label: "7 days" },
	{ value: "30d", label: "30 days" },
];

let resourceId = 0;
let resourceKind = "activity";

function el<T extends HTMLElement>(id: string): T | null {
	return document.getElementById(id) as T | null;
}

function formatDate(iso: string): string {
	const d = new Date(iso);
	return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** How often this link's page was opened, and when last. The leak signal: an unexpected count, or a
 * dormant link with a recent timestamp, is what tells the owner the URL got out. Nothing here identifies
 * a viewer — the server stores no per-open row (see ShareLink.OpenCount). */
function formatOpens(l: ShareLinkSummaryDto): string {
	if (!l.openCount) return "Not opened yet";
	const times = l.openCount === 1 ? "Opened once" : `Opened ${l.openCount}×`;
	return l.lastOpenedAt ? `${times} · last ${formatDate(l.lastOpenedAt)}` : times;
}

/** The resource's lifetime opens, above the rows. Not summed from the rows on purpose: ended links are
 * capped server-side, so the rows can be a subset — and this is the number the card badge shows. Hidden at
 * zero, where each row already says "Not opened yet". */
function renderTotal(totalOpens: number): string {
	if (!totalOpens) return "";
	const views = totalOpens === 1 ? "1 view" : `${totalOpens} views`;
	return `<p class="share-dlg-total">${escapeHtml(views)} all-time, across every link ever created for this item.</p>`;
}

function renderLinks(links: readonly ShareLinkSummaryDto[], totalOpens: number): void {
	const list = el<HTMLElement>("shareLinkList");
	if (!list) return;

	if (links.length === 0) {
		list.innerHTML = '<p class="share-dlg-empty">No share links yet.</p>';
		return;
	}

	// eslint-disable-next-line no-unsanitized/property -- every interpolation is escapeHtml()'d or Number()-coerced (FE-10)
	list.innerHTML = renderTotal(totalOpens) + links
		.map((l) => {
			const state = l.revokedAt
				? `<span class="share-dlg-state share-dlg-state--off">Revoked</span>`
				: l.isActive
					? `<span class="share-dlg-state share-dlg-state--on">Active</span>`
					: `<span class="share-dlg-state share-dlg-state--off">Expired</span>`;
			// No token is shown: it is unrecoverable by design. Number() pins the one interpolation the
			// row builds from API JSON — the id is numeric by contract, and coercing keeps a contract
			// drift to a string id from becoming markup injection (FE-10).
			const revoke = l.isActive
				? `<button type="button" class="btn-cockpit btn-cockpit-danger btn-cockpit-sm"
				           data-revoke-id="${Number(l.id)}">Revoke</button>`
				: "";
			return `<div class="share-dlg-row">
				${state}
				<span class="share-dlg-expiry">Expires ${escapeHtml(formatDate(l.expiresAt))}</span>
				<span class="share-dlg-opens">${escapeHtml(formatOpens(l))}</span>
				${revoke}
			</div>`;
		})
		.join("");
}

async function loadLinks(): Promise<void> {
	try {
		const resp = await fetch(`/api/sharelinks?kind=${resourceKind}&resourceId=${resourceId}`);
		if (!resp.ok) return;
		const body: ShareLinkListResponse = await resp.json();
		renderLinks(body.links ?? [], body.totalOpens ?? 0);
	} catch {
		/* leave the previous list in place */
	}
}

function showMintedUrl(url: string): void {
	const box = el<HTMLElement>("shareLinkResult");
	const input = el<HTMLInputElement>("shareLinkUrl");
	if (!box || !input) return;

	// Absolute only for the user's convenience in copying; the server deliberately returned a relative URL.
	input.value = new URL(url, window.location.origin).toString();
	box.hidden = false;
	input.select();
}

// In-flight guard: a double-click on Create must not mint TWO live tokens — the first would be shown
// nowhere (the plaintext exists once, in its mint response) and would sit as an unexplained extra grant
// revocable only blind.
let minting = false;

async function mint(): Promise<void> {
	if (minting) return;
	minting = true;
	const createBtn = el<HTMLButtonElement>("shareLinkCreate");
	if (createBtn) createBtn.disabled = true;
	const ttl = el<HTMLSelectElement>("shareLinkTtl")?.value ?? "7d";
	const status = el<HTMLElement>("shareLinkStatus");
	if (status) status.textContent = "";

	try {
		const resp = await fetch("/api/sharelinks", {
			method: "POST",
			headers: { "Content-Type": "application/json" },
			body: JSON.stringify({ kind: resourceKind, resourceId, ttl }),
		});

		if (!resp.ok) {
			if (status) {
				status.textContent = await readApiErrorMessage(resp, "Could not create a share link.");
			}
			return;
		}

		const minted: ShareLinkMintResponse = await resp.json();
		showMintedUrl(minted.url);
		await loadLinks();
	} catch {
		if (status) status.textContent = "Could not create a share link.";
	} finally {
		minting = false;
		if (createBtn) createBtn.disabled = false;
	}
}

/** Revoke one link by id. Exported because the /Sharing inventory revokes the same links from a different
 * page — one fetch, so a change to the endpoint or its failure handling cannot land in only one of them. */
export async function revokeShareLink(id: number): Promise<boolean> {
	try {
		const resp = await fetch(`/api/sharelinks/${id}`, { method: "DELETE" });
		return resp.ok;
	} catch {
		return false;
	}
}

async function revoke(id: number): Promise<void> {
	if (await revokeShareLink(id)) await loadLinks();
}

async function copyUrl(): Promise<void> {
	const input = el<HTMLInputElement>("shareLinkUrl");
	const btn = el<HTMLButtonElement>("shareLinkCopy");
	if (!input || !btn) return;

	try {
		await navigator.clipboard.writeText(input.value);
	} catch {
		// Clipboard API needs a secure context; the select() fallback lets the user copy manually.
		input.select();
		return;
	}

	const original = btn.textContent;
	btn.textContent = "Copied";
	window.setTimeout(() => {
		btn.textContent = original;
	}, 1500);
}

/** Which resource this dialog mints links for. */
export interface ShareLinksOptions {
	/** Wire kind — must match ShareKindNames on the server ("activity" | "segment" | "live" | "race"). */
	kind: string;
	resourceId: number;
}

export function setupShareLinks(opts: ShareLinksOptions): void {
	const openBtn = el<HTMLButtonElement>("shareActivityBtn");
	const dialog = el<HTMLDialogElement>("shareLinkDialog");
	if (!openBtn || !dialog) return; // not the owner — the markup is not rendered

	resourceId = opts.resourceId;
	resourceKind = opts.kind;

	const ttlSelect = el<HTMLSelectElement>("shareLinkTtl");
	if (ttlSelect) {
		// eslint-disable-next-line no-unsanitized/property -- TTL_OPTIONS is a module constant, labels escapeHtml()'d
		ttlSelect.innerHTML = TTL_OPTIONS.map(
			(o) => `<option value="${o.value}">${escapeHtml(o.label)}</option>`,
		).join("");
		ttlSelect.value = "7d";
	}

	openBtn.addEventListener("click", () => {
		const result = el<HTMLElement>("shareLinkResult");
		if (result) result.hidden = true; // a previous link is never re-shown
		const status = el<HTMLElement>("shareLinkStatus");
		if (status) status.textContent = "";
		void loadLinks();
		dialog.showModal();
	});

	el<HTMLButtonElement>("shareLinkCreate")?.addEventListener("click", () => void mint());
	el<HTMLButtonElement>("shareLinkCopy")?.addEventListener("click", () => void copyUrl());
	el<HTMLButtonElement>("shareLinkClose")?.addEventListener("click", () => dialog.close());

	// Delegated: rows are re-rendered on every list refresh.
	el<HTMLElement>("shareLinkList")?.addEventListener("click", (e) => {
		const btn = (e.target as HTMLElement).closest<HTMLElement>("[data-revoke-id]");
		if (!btn) return;
		const id = Number(readData(btn, 'revokeId'));
		if (Number.isFinite(id)) void revoke(id);
	});
}
