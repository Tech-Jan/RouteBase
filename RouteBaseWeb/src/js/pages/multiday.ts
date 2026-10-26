// Multi-Day Tours page (B7): wires each card's delete button to DELETE /api/tours/{id}.
// Deleting a tour removes the tour ROW only — its activities are kept and lose the tour link
// (the confirm text says exactly that). Cards are server-rendered; on success the card is
// removed in place, and the page reloads when the last one goes so the server-rendered
// empty state appears.

import { readData } from '../core/data-attrs';
import type { TourDeleteResponse } from '../generated/contracts';

function deleteTour(btn: HTMLButtonElement): void {
	const id = Number(readData(btn, 'tourId'));
	if (!Number.isFinite(id)) return;
	const name = readData(btn, 'tourName') || "this tour";
	if (!confirm(`Delete "${name}"? Its activities are kept — they only lose the tour link.`)) return;

	btn.disabled = true;
	fetch("/api/tours/" + id, { method: "DELETE" })
		.then((r) => {
			if (!r.ok) throw new Error("delete failed");
			const body: Promise<TourDeleteResponse> = r.json();
			return body;
		})
		.then((body) => {
			document.querySelector(`.tour-card[data-tour-id="${body.tourId}"]`)?.remove();
			if (!document.querySelector(".tour-card")) location.reload();
		})
		.catch(() => {
			alert("Failed to delete the tour.");
			btn.disabled = false;
		});
}

document.querySelectorAll<HTMLButtonElement>(".tour-delete-btn").forEach((btn) => {
	btn.addEventListener("click", () => deleteTour(btn));
});
