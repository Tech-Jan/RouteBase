import { beforeEach, describe, expect, it, vi } from 'vitest';
import { initRaceSettingsPanel } from './race-settings-panel';

/**
 * The owner-only "Who can edit" dropdown (a map-toolbar control). Picking an option PUTs the policy and,
 * if the write is refused, rolls the active choice back so the UI never claims a change the server
 * rejected.
 */
function mount(active = 'owner'): HTMLDetailsElement {
	document.body.innerHTML = `
		<details class="map-dd race-editpolicy" id="raceEditPolicy" data-race-id="1">
			<summary class="map-dd-summary">
				<span class="map-dd-value" id="raceEditPolicyValue">Only me</span>
			</summary>
			<div class="map-dd-menu">
				<button class="map-dd-item" data-policy="owner" data-short="Only me">Only me</button>
				<button class="map-dd-item" data-policy="friends" data-short="Friends">My friends</button>
				<button class="map-dd-item" data-policy="public" data-short="Anyone">Anyone who can view</button>
				<p class="race-editpolicy-msg" id="raceEditPolicyMsg" hidden></p>
			</div>
		</details>`;
	const dropdown = document.getElementById('raceEditPolicy') as HTMLDetailsElement;
	dropdown.querySelector(`[data-policy="${active}"]`)!.classList.add('active');
	dropdown.open = true;
	initRaceSettingsPanel(1);
	return dropdown;
}

function click(dropdown: HTMLDetailsElement, policy: string): void {
	dropdown.querySelector<HTMLButtonElement>(`[data-policy="${policy}"]`)!.click();
}

describe('the edit-policy dropdown', () => {
	beforeEach(() => {
		vi.stubGlobal('fetch', vi.fn(async () => new Response('', { status: 200 })));
	});

	it('does nothing when its dropdown is absent (non-owner page)', () => {
		document.body.innerHTML = '<div></div>';
		expect(() => initRaceSettingsPanel(1)).not.toThrow();
	});

	it('PUTs the chosen policy and moves the active marker + summary label', async () => {
		const dropdown = mount('owner');

		click(dropdown, 'friends');

		// Wait on the SETTLED state — the active marker moves only after the PUT resolves.
		await vi.waitFor(() =>
			expect(dropdown.querySelector('[data-policy="friends"]')!.classList.contains('active')).toBe(true));
		expect(fetch).toHaveBeenCalledWith('/api/RacesApi/1/edit-policy', expect.objectContaining({
			method: 'PUT',
			body: JSON.stringify({ policy: 'friends' }),
		}));
		expect(dropdown.querySelector('[data-policy="owner"]')!.classList.contains('active')).toBe(false);
		expect(document.getElementById('raceEditPolicyValue')!.textContent).toBe('Friends');
	});

	it('rolls the choice back and keeps the menu open when the write is refused', async () => {
		vi.stubGlobal('fetch', vi.fn(async () =>
			new Response(JSON.stringify({ error: 'nope' }), { status: 404 })));
		const dropdown = mount('owner');

		click(dropdown, 'public');

		await vi.waitFor(() =>
			expect(document.getElementById('raceEditPolicyMsg')!.textContent).toContain('nope'));
		expect(dropdown.querySelector('[data-policy="owner"]')!.classList.contains('active')).toBe(true);
		expect(dropdown.querySelector('[data-policy="public"]')!.classList.contains('active')).toBe(false);
		expect(dropdown.open).toBe(true);
	});
});
