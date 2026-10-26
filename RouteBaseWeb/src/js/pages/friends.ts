// friends.ts — Friends page: search, accept/decline requests, remove friends.
// No inline onclick / window shims: buttons carry data-action/data-id/data-name
// and are wired via delegated listeners (search results build DOM with textContent).

import { readData } from '../core/data-attrs';
import { FRIEND_REQUEST_RESOLVED_EVENT } from '../core/dom-events';
import type { UserSearchResult, FriendActionResponse } from '../generated/contracts';

/** One search hit — generated from C# UserSearchResult (Models/UserSearchResult.cs). */
type FriendSearchResult = UserSearchResult;

// PI-03: "Requested"/"Friends Since" were raw server UTC ISO strings printed verbatim — render
// them in the browser's own local time instead (the server's clock reads UTC in the Docker deployment).
document.querySelectorAll<HTMLElement>("[data-utc-ts]").forEach((el) => {
	const iso = readData(el, 'utcTs');
	if (iso) el.textContent = new Date(iso).toLocaleDateString();
});

// FriendActionResponse now imported from generated contracts (body is unread; callers show their own toast text).

const searchInput = document.getElementById("searchInput") as HTMLInputElement;
const searchBtn   = document.getElementById("searchBtn") as HTMLButtonElement;

searchInput?.addEventListener("keydown", (e: KeyboardEvent) => {
	if (e.key === "Enter") searchUsers();
});
searchBtn?.addEventListener("click", () => searchUsers());

// Delegated actions for the server-rendered request/friend rows.
document.addEventListener("click", (e: MouseEvent) => {
	const btn = (e.target as HTMLElement).closest<HTMLElement>("[data-action]");
	if (!btn) return;
	const id = Number(readData(btn, 'id'));
	switch (readData(btn, 'action')) {
		case "accept":  acceptRequest(id); break;
		case "decline": declineRequest(id); break;
		case "remove":  removeFriend(id, readData(btn, 'name') ?? "this friend"); break;
	}
});

// ── Search ───────────────────────────────────────────────────────

function searchUsers(): void {
	const q = searchInput.value.trim();
	if (!q) return;
	// In-flight guard: the Enter key bypasses the disabled BUTTON, so without this two searches
	// could overlap and the older response could render over the newer query's results.
	if (searchBtn.disabled) return;

	searchBtn.disabled = true;
	searchBtn.textContent = 'Searching…';

	fetch("/api/friends/search?q=" + encodeURIComponent(q))
		.then((r) => {
			if (!r.ok) throw new Error("HTTP " + r.status); // a JSON error body is not a result list
			return r.json();
		})
		.then((results: FriendSearchResult[]) => renderSearchResults(results))
		.catch(() => showSearchError("Search failed. Please try again."))
		.finally(() => {
			searchBtn.disabled = false;
			searchBtn.textContent = 'Search';
		});
}

function renderSearchResults(results: FriendSearchResult[]): void {
	const container = document.getElementById("searchResults");
	if (!container) return;
	container.replaceChildren();

	if (!results || results.length === 0) {
		const p = document.createElement("p");
		p.className = "friends-no-results";
		p.textContent = "No users found.";
		container.appendChild(p);
		return;
	}

	const table = document.createElement("table");
	table.className = "friends-table";
	table.innerHTML =
		'<thead><tr><th>Name</th><th class="col-hide-mobile">Email</th>' +
		'<th class="col-actions">Status</th></tr></thead>';

	const tbody = document.createElement("tbody");
	for (const u of results) {
		const name = u.displayName || u.email || u.userId;
		const tr = document.createElement("tr");

		const nameTd = document.createElement("td");
		nameTd.textContent = name;

		const emailTd = document.createElement("td");
		emailTd.className = "col-meta col-hide-mobile";
		emailTd.textContent = u.email || "";

		const actionTd = document.createElement("td");
		actionTd.className = "col-actions";
		if (u.friendshipStatus === "accepted" || u.friendshipStatus === "pending") {
			const chip = document.createElement("span");
			chip.className = "chip";
			chip.textContent = u.friendshipStatus === "accepted" ? "Friends" : "Request Sent";
			actionTd.appendChild(chip);
		} else {
			const addBtn = document.createElement("button");
			addBtn.className = "btn-cockpit btn-cockpit-ghost btn-cockpit-xs";
			addBtn.textContent = "Add Friend";
			addBtn.addEventListener("click", () => sendRequest(u.userId, name));
			actionTd.appendChild(addBtn);
		}

		tr.append(nameTd, emailTd, actionTd);
		tbody.appendChild(tr);
	}
	table.appendChild(tbody);
	container.appendChild(table);
}

function showSearchError(msg: string): void {
	const container = document.getElementById("searchResults");
	if (!container) return;
	container.replaceChildren();
	const p = document.createElement("p");
	p.className = "friends-error";
	p.textContent = msg;
	container.appendChild(p);
}

// SO-04: guard accept/decline/send against overlapping in-flight duplicates (fast double-click).
const inFlightIds = new Set<number>();
const inFlightUsers = new Set<string>();

// SO-03: after a pending request is resolved, drop its row, decrement the header count, remove the
// whole section when the last one goes, and nudge the nav badge to re-fetch.
function onPendingRowRemoved(id: number): void {
	const row = document.getElementById("pending-row-" + id);
	if (row) row.remove();
	const remaining = document.querySelectorAll("#pendingRequestsBody tr").length;
	const countEl = document.getElementById("pendingCount");
	if (countEl) countEl.textContent = String(remaining);
	if (remaining === 0) document.getElementById("pendingRequestsBody")?.closest("section")?.remove();
	document.dispatchEvent(new CustomEvent(FRIEND_REQUEST_RESOLVED_EVENT));
}

// ── Accept ───────────────────────────────────────────────────────

function acceptRequest(id: number): void {
	if (inFlightIds.has(id)) return;
	inFlightIds.add(id);
	apiPost("/api/friends/" + id + "/accept")
		.then(() => {
			onPendingRowRemoved(id);
			showToast("Friend request accepted!", "success");
		})
		.catch((status: unknown) =>
			showToast(
				status === 404
					? "This request is no longer available — it may have been withdrawn."
					: "Failed to accept request.",
				"danger",
			),
		)
		.finally(() => inFlightIds.delete(id));
}

// ── Decline ──────────────────────────────────────────────────────

function declineRequest(id: number): void {
	if (inFlightIds.has(id)) return;
	inFlightIds.add(id);
	apiPost("/api/friends/" + id + "/decline")
		.then(() => {
			onPendingRowRemoved(id);
			showToast("Request declined.", "secondary");
		})
		.catch((status: unknown) =>
			showToast(
				status === 404
					? "This request is no longer available — it may have been withdrawn."
					: "Failed to decline request.",
				"danger",
			),
		)
		.finally(() => inFlightIds.delete(id));
}

// ── Remove ───────────────────────────────────────────────────────

function removeFriend(id: number, name: string): void {
	if (!confirm("Remove " + name + " from your friends?")) return;
	fetch("/api/friends/" + id, { method: "DELETE" })
		.then((r) => {
			// API-07: failures are a bodiless 404 (missing and not-yours both) — throw the status.
			if (!r.ok) throw r.status;
			return r.json();
		})
		.then(() => {
			const row = document.getElementById("friend-row-" + id);
			if (row) row.remove();
			const remaining = document.querySelectorAll("#friendsBody tr").length;
			const count = document.getElementById("friendsCount");
			if (count) count.textContent = String(remaining);
			if (remaining === 0) {
				const table = document.getElementById("friendsTable");
				if (table) table.remove();
				const noMsg = document.getElementById("noFriendsMsg");
				if (!noMsg) {
					const body = document.getElementById("friends-list-body");
					if (body)
						body.innerHTML =
							'<div id="noFriendsMsg" class="empty-state" style="margin:16px 20px;">' +
							'No friends yet. Use the search below to find and add people.</div>';
				}
			}
			showToast("Friend removed.", "secondary");
		})
		.catch((status: unknown) =>
			showToast(
				status === 404 ? "Friendship not found — it may already be removed." : "Failed to remove friend.",
				"danger",
			),
		);
}

// ── Send Request ─────────────────────────────────────────────────

function sendRequest(userId: string, name: string): void {
	if (inFlightUsers.has(userId)) return; // SO-04: ignore overlapping duplicate send
	inFlightUsers.add(userId);
	fetch("/api/friends/request", {
		method: "POST",
		headers: { "Content-Type": "application/json" },
		body: JSON.stringify({ addresseeId: userId }),
	})
		.then((r) => {
			if (!r.ok) return r.json().then((b) => { throw b.message || "Error"; });
			return r.json();
		})
		.then(() => {
			showToast("Friend request sent to " + name + "!", "success");
			searchUsers();
		})
		.catch((msg: unknown) =>
			showToast(typeof msg === "string" ? msg : "Failed to send request.", "danger"),
		)
		.finally(() => inFlightUsers.delete(userId));
}

// ── Helpers ──────────────────────────────────────────────────────

function apiPost(url: string): Promise<FriendActionResponse> {
	// API-07: accept/decline failures are a bodiless 404 (missing/not-yours/already-handled all
	// collapse, like the rest of the API) — throw the status; callers pick their own copy.
	return fetch(url, { method: "POST" }).then((r) => {
		if (!r.ok) throw r.status;
		return r.json();
	});
}

let toastContainer: HTMLElement | null = null;
function showToast(msg: string, type: string): void {
	if (!toastContainer) {
		toastContainer = document.createElement("div");
		toastContainer.className = "cockpit-toast-container";
		document.body.appendChild(toastContainer);
	}
	const toast = document.createElement("div");
	toast.className = "cockpit-toast cockpit-toast--" + type;
	toast.textContent = msg;
	toastContainer.appendChild(toast);
	setTimeout(() => { if (toast.parentNode) toast.parentNode.removeChild(toast); }, 4000);
}

export {};
