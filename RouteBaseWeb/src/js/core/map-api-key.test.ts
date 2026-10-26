import { describe, it, expect, afterEach } from 'vitest';
import { getMapTilerApiKey, setMapTilerApiKey, resetMapTilerApiKey } from './map-api-key';

// The MapTiler key seam that replaced window.MAPTILER_API_KEY. The regression pin is that
// readers work with NO window global present at all — this whole file never touches `window`.
afterEach(() => resetMapTilerApiKey());

describe('map-api-key seam', () => {
	it('defaults to the keyless empty string before any init (no window global involved)', () => {
		expect(getMapTilerApiKey()).toBe('');
	});

	it('setMapTilerApiKey stores the key for getMapTilerApiKey', () => {
		setMapTilerApiKey('abc123');
		expect(getMapTilerApiKey()).toBe('abc123');
	});

	it('nullish keys normalise to the keyless empty string', () => {
		setMapTilerApiKey(null);
		expect(getMapTilerApiKey()).toBe('');
		setMapTilerApiKey(undefined);
		expect(getMapTilerApiKey()).toBe('');
	});

	it('resetMapTilerApiKey clears a previously set key (no cross-case leak)', () => {
		setMapTilerApiKey('abc123');
		resetMapTilerApiKey();
		expect(getMapTilerApiKey()).toBe('');
	});
});
