import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
	showDesyncBanner, removeDesyncBanner, injectKpiChip,
	setEditSessionParam,
} from './edit-mode-chrome';

describe('edit-mode-chrome', () => {
	beforeEach(() => {
		document.body.innerHTML = '';
		history.replaceState(null, '', '/Activities/Details/7');
	});

	it('showDesyncBanner is idempotent', () => {
		showDesyncBanner(false, () => {});
		showDesyncBanner(false, () => {});
		expect(document.querySelectorAll('#trackEditDesyncBanner')).toHaveLength(1);
	});

	it('showDesyncBanner offers Retry (wired to onRetry) or Reload when the session is gone', () => {
		const onRetry = vi.fn();
		showDesyncBanner(false, onRetry);
		const retry = document.querySelector('#trackEditDesyncBanner button') as HTMLButtonElement;
		expect(retry.textContent).toBe('Retry');
		retry.click();
		expect(onRetry).toHaveBeenCalledTimes(1);

		removeDesyncBanner();
		expect(document.getElementById('trackEditDesyncBanner')).toBeNull();

		showDesyncBanner(true, onRetry);
		expect((document.querySelector('#trackEditDesyncBanner button') as HTMLButtonElement).textContent)
			.toBe('Reload page');
	});

	it('injectKpiChip needs a .kpi-strip parent and never duplicates', () => {
		injectKpiChip();
		expect(document.getElementById('trackEditKpiChip')).toBeNull(); // no strip → no chip

		document.body.innerHTML = '<div><div class="kpi-strip"></div></div>';
		injectKpiChip();
		injectKpiChip();
		expect(document.querySelectorAll('#trackEditKpiChip')).toHaveLength(1);
		// It is inserted BEFORE the strip, inside the strip's parent.
		expect(document.querySelector('.kpi-strip')?.previousElementSibling?.id).toBe('trackEditKpiChip');
	});

	it('setEditSessionParam writes the URL param', () => {
		setEditSessionParam('abc123');
		expect(new URL(window.location.href).searchParams.get('editSession')).toBe('abc123');
	});

});
