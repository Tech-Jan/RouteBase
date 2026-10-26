// dashboard-widgets.ts — right-rail Segments + Friends widgets.

import { escHtml, replaceWidgetContent } from './dashboard-shared';
import type { SegmentCardDto } from '../generated/contracts';

interface FriendItem {
	userId: string;
	displayName: string | null;
	friendsSince: string;
	friendshipId: number;
}

function getInitials(name: string | null): string {
	if (!name) return "?";
	const parts = name.trim().split(/\s+/);
	const first = parts[0];
	const last = parts[parts.length - 1];
	// charAt: '' for an impossible empty part (trim precedes the split), string-typed either way.
	return parts.length >= 2 && first && last
		? (first.charAt(0) + last.charAt(0)).toUpperCase()
		: name.charAt(0).toUpperCase();
}

/** Rows the segments card shows. The server enforces its own bound; this is the widget's ask. */
const CARD_COUNT = 3;

function fmtDistance(metres: number): string {
	return metres >= 1000
		? (metres / 1000).toFixed(1) + " km"
		: Math.round(metres) + " m";
}

export async function fetchSegments(): Promise<void> {
	const el = document.getElementById("segments-widget");
	if (!el) return;
	try {
		// The card list, sized server-side (PERF-4): this used to fetch EVERY visible segment — full
		// rows, all of them — to paint three cards.
		const resp = await fetch(`/api/SegmentsApi/cards?limit=${CARD_COUNT}`);
		if (!resp.ok) throw new Error("HTTP " + resp.status);
		const top3: SegmentCardDto[] = await resp.json();

		const inner = document.createElement("div");
		inner.style.cssText = "display:flex;flex-direction:column;gap:10px;";

		if (top3.length === 0) {
			inner.innerHTML =
				'<div class="widget-empty mono">No segments yet — <a href="/Segments">create one</a></div>';
		} else {
			// eslint-disable-next-line no-unsanitized/property -- segmentName escHtml'd in the row template; segmentId numeric; distances fmtDistance numerics
			inner.innerHTML = top3
				.map(
					(s) => `
				<a href="/Segments/${s.segmentId}" class="segment-row" style="text-decoration:none;color:inherit;">
					<div class="segment-icon-wrap">
						<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="var(--acc-cyan)" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
							<polyline points="22 12 18 12 15 21 9 3 6 12 2 12"/>
						</svg>
					</div>
					<div class="segment-info">
						<div class="segment-name">${escHtml(s.segmentName ?? "Unnamed")}</div>
						<div class="segment-meta">${fmtDistance(s.segmentDistance ?? 0)}${(s.segmentAscent ?? 0) > 0 ? " · +" + Math.round(s.segmentAscent ?? 0) + "m" : ""}</div>
					</div>
				</a>`,
				)
				.join("");
		}

		replaceWidgetContent(el, inner);
	} catch (e) {
		console.warn("Segments fetch failed:", e);
		// Without this the widget's initial skeleton shimmer (Index.cshtml) would spin forever on a
		// 500/network failure. Swap it for a quiet failure message instead of a permanent "loading".
		replaceWidgetContent(el, widgetError("Couldn't load segments."));
	}
}

/** A muted failure message reusing the widget empty-state style (dashboard.css .widget-empty). */
function widgetError(text: string): HTMLElement {
	const div = document.createElement("div");
	div.className = "widget-empty mono";
	div.textContent = text;
	return div;
}

export async function fetchFriendsRecent(): Promise<void> {
	const el = document.getElementById("friends-widget");
	if (!el) return;
	try {
		const resp = await fetch("/api/friends");
		if (!resp.ok) throw new Error("HTTP " + resp.status);
		const data: FriendItem[] = await resp.json();
		const top5 = data.slice(0, 5);

		const inner = document.createElement("div");
		inner.style.cssText = "display:flex;flex-direction:column;gap:8px;";

		if (top5.length === 0) {
			inner.innerHTML =
				'<div class="widget-empty mono">No friends yet — <a href="/Friends">find some</a></div>';
		} else {
			// eslint-disable-next-line no-unsanitized/property -- displayName/initials/friendsSince escHtml'd in the row template
			inner.innerHTML = top5
				.map(
					(f) => `
				<div class="friend-row">
					<div class="friend-avatar" style="background:var(--bg-3);">${escHtml(getInitials(f.displayName))}</div>
					<div class="friend-info">
						<div class="friend-name">${escHtml(f.displayName ?? "Unknown")}</div>
						<div class="friend-last mono">Since ${escHtml(f.friendsSince)}</div>
					</div>
				</div>`,
				)
				.join("");
		}

		replaceWidgetContent(el, inner);
	} catch (e) {
		console.warn("Friends fetch failed:", e);
		replaceWidgetContent(el, widgetError("Couldn't load friends."));
	}
}
