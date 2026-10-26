import { describe, expect, it } from 'vitest';
import {
	renderVisibilityBadge,
	visibilityIcon,
	visibilityLabel,
	visibilityVariant,
} from './visibility-badge';

describe('visibility-badge', () => {
	it('maps each of the three values to its own icon, label and variant', () => {
		expect(visibilityIcon('private')).toBe('bi-lock');
		expect(visibilityIcon('friends')).toBe('bi-people');
		expect(visibilityIcon('public')).toBe('bi-globe');

		expect(visibilityLabel('private')).toBe('Private');
		expect(visibilityLabel('friends')).toBe('Friends');
		expect(visibilityLabel('public')).toBe('Public');

		expect(visibilityVariant('private')).toBe('det-badge--danger');
		expect(visibilityVariant('friends')).toBe('det-badge--info');
		expect(visibilityVariant('public')).toBe('det-badge--success');
	});

	it('falls back to friends for an unknown value rather than rendering blank', () => {
		// A blank chip would read as "no visibility", which is not a state this app has.
		expect(visibilityIcon('nonsense')).toBe('bi-people');
		expect(visibilityLabel('')).toBe('Friends');
		expect(visibilityVariant('PUBLIC')).toBe('det-badge--info'); // case-sensitive by design
	});

	it('repaints an existing badge in place, leaving no stale variant class', () => {
		const badge = document.createElement('span');
		badge.className = 'det-badge det-badge--danger';
		badge.innerHTML = '<i class="bi bi-lock"></i> Private';

		renderVisibilityBadge(badge, 'public');

		// THE regression this guards: className is assigned, not added to, so --danger cannot survive.
		expect(badge.className).toBe('det-badge det-badge--success');
		expect(badge.classList.contains('det-badge--danger')).toBe(false);
		expect(badge.textContent?.trim()).toBe('Public');
		expect(badge.querySelector('i')?.className).toBe('bi bi-globe');
	});
});
