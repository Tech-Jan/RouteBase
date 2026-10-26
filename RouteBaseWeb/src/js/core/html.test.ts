import { describe, it, expect } from 'vitest';
import { escapeHtml, getRankingBadge } from './html';

describe('DOM/HTML helpers', () => {
	it('escapeHtml escapes < > &', () => {
		expect(escapeHtml('a<b>&c')).toBe('a&lt;b&gt;&amp;c');
	});

	it('escapeHtml escapes & < > " (attribute-safe — DT-12)', () => {
		expect(escapeHtml('& < > "')).toBe('&amp; &lt; &gt; &quot;');
	});

	it('escapeHtml escapes single quotes for attribute contexts (CR2-01)', () => {
		expect(escapeHtml("it's a 'test'")).toBe('it&#39;s a &#39;test&#39;');
	});

	it('escapeHtml is null-safe (CR2-01)', () => {
		expect(escapeHtml(null)).toBe('');
		expect(escapeHtml(undefined)).toBe('');
	});

	it('escapeHtml leaves backticks alone (not an HTML attribute delimiter — documented decision)', () => {
		expect(escapeHtml('`raw`')).toBe('`raw`');
	});

	it('escaped value survives a double-quoted attribute round-trip', () => {
		const el = document.createElement('div');
		el.innerHTML = '<span title="' + escapeHtml('" onmouseover="x') + '">t</span>';
		expect(el.firstElementChild!.getAttribute('title')).toBe('" onmouseover="x');
		expect(el.querySelectorAll('*').length).toBe(1);
	});

	it('escaped value survives a single-quoted attribute round-trip', () => {
		const el = document.createElement('div');
		el.innerHTML = "<span title='" + escapeHtml("' onclick='y") + "'>t</span>";
		expect(el.firstElementChild!.getAttribute('title')).toBe("' onclick='y");
		expect(el.querySelectorAll('*').length).toBe(1);
	});

	it('getRankingBadge returns medal badges for 1-3, plain #N otherwise', () => {
		expect(getRankingBadge(1)).toContain('#1');
		expect(getRankingBadge(1)).toContain('bg-warning');
		expect(getRankingBadge(2)).toContain('#2');
		expect(getRankingBadge(3)).toContain('#3');
		expect(getRankingBadge(7)).toBe('<strong>#7</strong>');
	});
});
