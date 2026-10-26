import { describe, it, expect } from 'vitest';
import { ShapeFilterPanel, type PanelDeps } from './shape-filter-panel';

const noopDeps: PanelDeps = {
	onToggleActive: () => {},
	onToolChange: () => {},
	onZoneRoleChange: () => {},
	onGateRoleChange: () => {},
	onGateDirectionChange: () => {},
	onMatchModeChange: () => {},
	onGateDebounceChange: () => {},
	onAdoptedFiltersChange: () => {},
	onDeleteSelected: () => {},
	onClear: () => {},
	onSave: () => {},
};

function makePanel(): { panel: ShapeFilterPanel; saveBtn: HTMLButtonElement } {
	const host = document.createElement('div');
	const panel = new ShapeFilterPanel(host, noopDeps);
	const saveBtn = host.querySelector('.shape-btn--save') as HTMLButtonElement;
	return { panel, saveBtn };
}

describe('ShapeFilterPanel save gating (SP-01)', () => {
	it('keeps save disabled while saving even if setSaveEnabled(true) arrives', () => {
		const { panel, saveBtn } = makePanel();
		panel.setSaveEnabled(true);
		expect(saveBtn.disabled).toBe(false);

		panel.setSaving(true);
		expect(saveBtn.disabled).toBe(true);

		// A late preview result re-enabling save must not win over the in-flight save.
		panel.setSaveEnabled(true);
		expect(saveBtn.disabled).toBe(true);

		panel.setSaving(false);
		expect(saveBtn.disabled).toBe(false);
	});
});

describe('ShapeFilterPanel gate debounce control', () => {
	function mount(deps: Partial<PanelDeps> = {}): { panel: ShapeFilterPanel; sel: HTMLSelectElement } {
		const host = document.createElement('div');
		const panel = new ShapeFilterPanel(host, { ...noopDeps, ...deps });
		return { panel, sel: host.querySelector('.shape-select--gate-gap') as HTMLSelectElement };
	}

	it('starts at the server default, not at the first option', () => {
		// The option list leads with "Off" (0); without an explicit init the browser would select it
		// and the panel would silently claim a debounce the server does not use.
		expect(mount().sel.value).toBe('15');
	});

	it('is hidden unless the extent is "between gates"', () => {
		const { panel, sel } = mount();
		expect(sel.hidden).toBe(true); // panel starts on 'whole'
		panel.setMatchMode('gates');
		expect(sel.hidden).toBe(false);
		panel.setMatchMode('clip');
		expect(sel.hidden).toBe(true);
	});

	it('reports the picked value as a number', () => {
		const seen: number[] = [];
		const { sel } = mount({ onGateDebounceChange: (s) => seen.push(s) });
		sel.value = '30';
		sel.dispatchEvent(new Event('change'));
		expect(seen).toEqual([30]);
	});

	it('shows a hand-authored value outside the presets rather than rendering blank', () => {
		// A stored doc can carry any number. Falling back to a blank select would misreport what the
		// segment actually matches with, and a resave would keep the unseen value.
		const { panel, sel } = mount();
		panel.setGateDebounce(7);
		expect(sel.value).toBe('7');
		expect(sel.selectedIndex).toBeGreaterThanOrEqual(0);
		// …and returning to a preset drops the one-off option again.
		panel.setGateDebounce(15);
		expect(sel.value).toBe('15');
		expect(Array.from(sel.options).some((o) => o.value === '7')).toBe(false);
	});
});
