// sharing-inventory.ts — the /Sharing page: every share link that is LIVE right now, in one list.
//
// The per-item share dialog (core/share-links.ts) answers "is THIS shared?"; a card badge answers it one
// row at a time. Neither answers "what am I publishing?", which is the question you ask when you think a
// URL got out — a link on a three-year-old ride is invisible until you scroll to its card. This page is
// that answer, and revoking from here is the action it leads to.
//
// Client-rendered: liveness is time-based, so a server-rendered list goes stale in an open tab, and
// re-fetching after each revoke keeps one render path instead of two.

import { readData } from '../core/data-attrs';
import { escapeHtml } from "../core/html";
import { revokeShareLink } from "../core/share-links";
// Generated from Models/ShareLinkContracts.cs.
import type { ShareLinkInventoryItemDto, ShareLinkInventoryResponse } from "../generated/contracts";

/** Wire kinds, in the order the page groups them. Must match ShareKindNames on the server. */
const KIND_ORDER = ["activity", "live", "segment", "race"] as const;

const KIND_LABELS: Readonly<Record<string, string>> = {
	activity: "Activities",
	live: "Live recordings",
	segment: "Segments",
	race: "Races",
};

/** Heading for a group of links. Unknown kinds keep their wire spelling rather than vanishing — a link
 * this build does not recognise is exactly the one an owner needs to see. */
export function kindLabel(kind: string): string {
	return KIND_LABELS[kind] ?? kind;
}

/** Where the owner goes to act on the shared thing. A live recording is keyed by its Activity shell, so it
 * shares the activity's page. Never a /s/ URL: the token is unrecoverable by design. */
export function resourceHref(kind: string, resourceId: number): string {
	const id = Number(resourceId);
	switch (kind) {
		case "segment":
			return `/Segments/Statistics?id=${id}`;
		case "race":
			return `/Races/Replay?raceId=${id}`;
		default:
			return `/Activities/Details/${id}`;
	}
}

export interface KindGroup {
	kind: string;
	label: string;
	links: readonly ShareLinkInventoryItemDto[];
}

/** Groups links by kind in KIND_ORDER, dropping empty groups. Kinds the server sends that this build does
 * not know about are appended rather than discarded. */
export function groupByKind(links: readonly ShareLinkInventoryItemDto[]): KindGroup[] {
	const known = KIND_ORDER.filter((k) => links.some((l) => l.kind === k));
	const extra = [...new Set(links.map((l) => l.kind))].filter(
		(k) => !(KIND_ORDER as readonly string[]).includes(k),
	);
	return [...known, ...extra].map((kind) => ({
		kind,
		label: kindLabel(kind),
		links: links.filter((l) => l.kind === kind),
	}));
}

function formatDate(iso: string): string {
	const d = new Date(iso);
	return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

/** How long this link still works. The number that decides whether to revoke now or let it lapse. */
export function formatExpiry(expiresAtIso: string, now: Date): string {
	const expires = new Date(expiresAtIso);
	if (Number.isNaN(expires.getTime())) return `Expires ${expiresAtIso}`;

	// Liveness is time-based and this list sits open in a tab: a link can lapse between fetch and read.
	if (expires.getTime() <= now.getTime()) return "Expired";

	const hours = Math.floor((expires.getTime() - now.getTime()) / 3_600_000);
	if (hours < 1) return "Expires within the hour";
	if (hours < 24) return `Expires in ${hours} hour${hours === 1 ? "" : "s"}`;
	const days = Math.floor(hours / 24);
	return `Expires in ${days} day${days === 1 ? "" : "s"}`;
}

/** How often the page behind this link was opened, and when last — the leak signal. Nothing here
 * identifies a viewer; the server stores no per-open row (see ShareLink.OpenCount). */
export function formatOpens(link: ShareLinkInventoryItemDto): string {
	if (!link.openCount) return "Not opened yet";
	const times = link.openCount === 1 ? "Opened once" : `Opened ${link.openCount}×`;
	return link.lastOpenedAt ? `${times} · last ${formatDate(link.lastOpenedAt)}` : times;
}

/** One row. Every interpolation is escaped or coerced to a number: the resource NAME is user-authored
 * (an activity title), so this is a stored-XSS sink (FE-10). */
function renderRow(link: ShareLinkInventoryItemDto, now: Date): string {
	return `<li class="share-inv-row">
		<a class="share-inv-name" href="${escapeHtml(resourceHref(link.kind, link.resourceId))}">${escapeHtml(link.resourceName)}</a>
		<span class="share-inv-expiry">${escapeHtml(formatExpiry(link.expiresAt, now))}</span>
		<span class="share-inv-opens">${escapeHtml(formatOpens(link))}</span>
		<button type="button" class="btn-cockpit btn-cockpit-danger btn-cockpit-sm"
		        data-revoke-id="${Number(link.id)}">Revoke</button>
	</li>`;
}

export function renderInventory(links: readonly ShareLinkInventoryItemDto[], now: Date): string {
	if (links.length === 0) {
		return `<p class="share-inv-empty">Nothing is shared right now. Links you create from an activity,
			segment, race or live recording will appear here until they expire or you revoke them.</p>`;
	}

	const count = links.length === 1 ? "1 live link" : `${links.length} live links`;
	const groups = groupByKind(links)
		.map(
			(g) => `<section class="share-inv-group">
				<h2 class="share-inv-kind">${escapeHtml(g.label)}</h2>
				<ul class="share-inv-list">${g.links.map((l) => renderRow(l, now)).join("")}</ul>
			</section>`,
		)
		.join("");

	return `<p class="share-inv-count">${escapeHtml(count)}</p>${groups}`;
}

async function load(container: HTMLElement): Promise<void> {
	container.setAttribute("aria-busy", "true");
	try {
		const resp = await fetch("/api/sharelinks/mine");
		if (!resp.ok) {
			container.innerHTML = `<p class="share-inv-error">Could not load your share links.</p>`;
			return;
		}
		const body: ShareLinkInventoryResponse = await resp.json();
		// eslint-disable-next-line no-unsanitized/property -- renderInventory escapeHtml's every string it interpolates (resourceName is user-authored); the id is Number()-coerced, the rest is literal
		container.innerHTML = renderInventory(body.links ?? [], new Date());
	} catch {
		container.innerHTML = `<p class="share-inv-error">Could not load your share links.</p>`;
	} finally {
		container.setAttribute("aria-busy", "false");
	}
}

export function setupSharingInventory(container: HTMLElement): void {
	// Delegated: rows are replaced wholesale on every refresh.
	container.addEventListener("click", (e) => {
		const btn = (e.target as HTMLElement).closest<HTMLElement>("[data-revoke-id]");
		if (!btn) return;
		const id = Number(readData(btn, 'revokeId'));
		if (!Number.isFinite(id)) return;

		(btn as HTMLButtonElement).disabled = true;
		// Re-fetch rather than dropping the row locally: another link may have lapsed while the page sat
		// open, and this list is only useful if it is true at the moment it is read. A failed revoke must
		// say so — the re-rendered row looking untouched read as a UI glitch while the link stayed live.
		void revokeShareLink(id).then(async (ok) => {
			await load(container);
			if (!ok)
				container.insertAdjacentHTML(
					"afterbegin",
					`<p class="share-inv-error">Could not revoke that link — it is still live. Try again.</p>`,
				);
		});
	});

	void load(container);
}

const root = document.getElementById("shareInventory");
if (root) setupSharingInventory(root);
