/**
 * edit-mode-chrome.ts — the editor's DOM/URL chrome: the desync banner, the KPI chip, the `?editSession`
 * URL param, and the plain navigation. Split out of edit-mode-controller.ts (TE-03,
 * it was 522 lines over a 400 soft cap): none of this touches the live EditorState/queue, so it is pure
 * DOM/URL work with its inputs passed in — unit-testable without the controller.
 */

/** Desync freeze banner (D-U2). Idempotent: a second call while it is up does nothing. */
export function showDesyncBanner(gone: boolean, onRetry: () => void): void {
	if (document.getElementById('trackEditDesyncBanner')) return;
	const banner = document.createElement('div');
	banner.id = 'trackEditDesyncBanner';
	banner.className = 'track-edit-desync-banner';
	const label = document.createElement('span');
	label.textContent = gone
		? 'This edit session was closed in another tab.'
		: 'Edits are not reaching the server.';
	const action = document.createElement('button');
	action.type = 'button';
	action.className = 'btn-cockpit btn-cockpit-sm';
	action.textContent = gone ? 'Reload page' : 'Retry';
	action.addEventListener('click', () => (gone ? window.location.reload() : onRetry()));
	banner.append(label, action);
	document.body.appendChild(banner);
}

export function removeDesyncBanner(): void {
	document.getElementById('trackEditDesyncBanner')?.remove();
}

/** "values = last saved state" chip above the KPI strip — the preview never rewrites the KPIs. */
export function injectKpiChip(): void {
	if (document.getElementById('trackEditKpiChip')) return;
	const strip = document.querySelector('.kpi-strip');
	if (!strip?.parentElement) return;
	const chip = document.createElement('div');
	chip.id = 'trackEditKpiChip';
	chip.className = 'track-edit-kpi-chip';
	chip.textContent = 'values = last saved state';
	strip.parentElement.insertBefore(chip, strip);
}

export function setEditSessionParam(sessionId: string): void {
	const url = new URL(window.location.href);
	url.searchParams.set('editSession', sessionId);
	history.replaceState(history.state, '', url.toString());
}

export function navigatePlain(activityId: number | undefined): void {
	window.location.href = `/Activities/Details/${activityId ?? ''}`;
}
