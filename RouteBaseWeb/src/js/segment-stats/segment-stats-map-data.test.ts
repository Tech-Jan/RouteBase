// @vitest-environment node
import { describe, it, expect } from 'vitest';
import { mapDataApi } from './segment-stats-map-data';
import type { SegmentEffort } from './segment-stats-effort-types';

// MH-01: the map effort popup is built via string concat + Popup.setHTML — activityName/date are
// cross-user content (friends' activities) and must be escaped at the sink.
describe('buildEffortPopupHtml — MH-01 XSS escaping', () => {
	const base = { rank: 1, time: 61, trackId: 1, activityId: 5 } as unknown as SegmentEffort;

	it('escapes a malicious activity name; a malicious date cannot reach the sink at all', () => {
		const html = mapDataApi.buildEffortPopupHtml({
			...base,
			name: '<img src=x onerror=1>',
			date: '<b>d</b>',
		} as SegmentEffort);
		expect(html).toContain('&lt;img');
		expect(html).not.toContain('<img');
		// B3: date is parsed + re-formatted (formatDateDMY), not echoed — a non-date renders as
		// nothing, so no tag (raw or escaped) survives to the sink.
		expect(html).not.toContain('<b>');
		expect(html).not.toContain('&lt;b&gt;');
		expect(html).toContain('Date: <br>');
		// Time still formatted HH:MM:SS from the numeric field.
		expect(html).toContain('00:01:01');
	});

	it('renders a plain name verbatim', () => {
		const html = mapDataApi.buildEffortPopupHtml({ ...base, name: 'Morning Ride', date: '2026-01-02' } as SegmentEffort);
		expect(html).toContain('Morning Ride');
	});
});
