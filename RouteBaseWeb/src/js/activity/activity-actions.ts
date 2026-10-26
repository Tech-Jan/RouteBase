/**
 * Activity Details — toolbar actions (Refresh Elevation, Delete).
 *
 * Extracted from the former inline <script> in Pages/Activities/Details.cshtml.
 * Buttons carry data-track-id / data-track-name attributes and are wired here
 * instead of via inline onclick=.
 *
 * Endpoints:
 *   POST   /api/activities/{id}/refresh-elevation
 *   DELETE /api/activities/{id}
 */

import { asyncCallback } from '../core/async-listener';
import { readData } from '../core/data-attrs';
import { deleteActivity } from '../core/activity-delete';
// Success body of POST /api/activities/{id}/refresh-elevation — generated from C# RefreshElevationResponse.
import type { ApiError, RefreshElevationResponse } from '../generated/contracts';

function wireRefreshElevation(): void {
	const btn = document.getElementById('refreshElevationBtn') as HTMLButtonElement | null;
	if (!btn) return;

	const original = btn.innerHTML;
	btn.addEventListener('click', asyncCallback('refresh-elevation click', async () => {
		const trackId = readData(btn, 'trackId');
		if (!trackId) return;

		btn.disabled = true;
		btn.innerHTML = '<span class="btn-spinner"></span> Refreshing...';
		try {
			const response = await fetch(`/api/activities/${trackId}/refresh-elevation`, {
				method: 'POST',
			});
			if (response.ok) {
				// EL-08: the server reports how many points actually got DEM elevation — zero means the
				// elevation source yielded nothing (altitudes unchanged), which must not look like success.
				const result = await response
					.json()
					.catch(() => null as RefreshElevationResponse | null);
				if (result && result.pointsReplaced === 0) {
					alert('Elevation source returned no data — altitudes are unchanged.');
				}
				window.location.reload();
				return;
			}
			const error = (await response.json().catch(() => ({}))) as Partial<ApiError>;
			alert(`Error refreshing elevation: ${error.message || response.statusText}`);
		} catch (err) {
			const msg = err instanceof Error ? err.message : String(err);
			alert(`Error refreshing elevation: ${msg}`);
		}
		btn.disabled = false;
		// eslint-disable-next-line no-unsanitized/property -- restores the button's own prior innerHTML (app-authored markup saved above)
		btn.innerHTML = original;
	}));
}

function wireDelete(): void {
	const btn = document.getElementById('deleteActivityBtn') as HTMLButtonElement | null;
	if (!btn) return;

	btn.addEventListener('click', () => {
		const trackId = readData(btn, 'trackId');
		const trackName = readData(btn, 'trackName') ?? '';
		if (!trackId) return;

		void deleteActivity(Number(trackId), trackName, {
			guardBtn: btn,
			onSuccess: () => {
				window.location.href = '/Activities';
			},
		});
	});
}

function init(): void {
	wireRefreshElevation();
	wireDelete();
}

if (document.readyState === 'loading') {
	document.addEventListener('DOMContentLoaded', init);
} else {
	init();
}
