// @vitest-environment node
import { describe, it, expect } from 'vitest';
import {
	polylineLayerSpec,
	combinedEffortLayerSpec,
	effortBaseLineWidth,
	effortBaseLineOpacity,
	effortHoverLineWidth,
	effortHoverLineOpacity,
} from './segment-stats-map-layer-specs';

// SS-layer-specs pins for the pure paint/layout literals extracted from the MapLibre engine.
// These lock the data-driven expressions verbatim (operators, thresholds, order) — a silent
// change to any threshold is a visible regression invisible to tsc.

// AddLayerObject is a union (LayerSpecification | CustomLayerInterface); the latter has no `paint`.
// These factories only ever return line layers, so narrow to read paint in assertions.
const paintOf = (spec: maplibregl.AddLayerObject): Record<string, unknown> =>
	(spec as { paint: Record<string, unknown> }).paint;

describe('polylineLayerSpec', () => {
	it('builds a line layer with color/width/opacity paint and round join/cap layout', () => {
		expect(polylineLayerSpec('seg-route', 'seg-route-src', '#2196F3', 5, 0.8)).toEqual({
			id: 'seg-route',
			type: 'line',
			source: 'seg-route-src',
			layout: { 'line-join': 'round', 'line-cap': 'round' },
			paint: { 'line-color': '#2196F3', 'line-width': 5, 'line-opacity': 0.8 },
		});
	});

	it('adds line-dasharray only when a dash array is passed', () => {
		const withDash = polylineLayerSpec('seg-bg', 'seg-bg-src', '#5aabff', 3, 0.6, [8, 6]);
		expect(paintOf(withDash)['line-dasharray']).toEqual([8, 6]);
		const noDash = polylineLayerSpec('seg-bg', 'seg-bg-src', '#5aabff', 3, 0.6);
		expect(paintOf(noDash)['line-dasharray']).toBeUndefined();
	});

	it('falls back to width 4 / opacity 0.8 when width or opacity is 0', () => {
		const p = paintOf(polylineLayerSpec('x', 'x-src', '#000', 0, 0));
		expect(p['line-width']).toBe(4);
		expect(p['line-opacity']).toBe(0.8);
	});
});

describe('combinedEffortLayerSpec', () => {
	it('builds the seg-efforts line layer with data-driven color/width/opacity', () => {
		expect(combinedEffortLayerSpec('seg-efforts-line', 'seg-efforts')).toEqual({
			id: 'seg-efforts-line',
			type: 'line',
			source: 'seg-efforts',
			layout: { 'line-join': 'round', 'line-cap': 'round' },
			paint: {
				'line-color': ['get', 'color'],
				'line-width': ['case', ['==', ['get', '_onSeg'], 1], 4, 2],
				'line-opacity': ['case', ['==', ['get', '_onSeg'], 1], 0.8, 0.25],
			},
		});
	});

	// The hover-leave handler resets to these base expressions; if the layer spec's base drifts from
	// the reset factories the hovered-then-left track paints at the wrong weight — invisible to tsc.
	it('base line-width/opacity stay identical to the effortBaseLine* reset factories', () => {
		const paint = paintOf(combinedEffortLayerSpec('seg-efforts-line', 'seg-efforts'));
		expect(paint['line-width']).toEqual(effortBaseLineWidth());
		expect(paint['line-opacity']).toEqual(effortBaseLineOpacity());
	});
});

describe('effort hover/base expressions', () => {
	it('base width/opacity are the on-segment vs off-segment case expressions', () => {
		expect(effortBaseLineWidth()).toEqual(['case', ['==', ['get', '_onSeg'], 1], 4, 2]);
		expect(effortBaseLineOpacity()).toEqual(['case', ['==', ['get', '_onSeg'], 1], 0.8, 0.25]);
	});

	it('hover width ramps hovered+on-segment 5, hovered 3, on-segment 4, else 2', () => {
		expect(effortHoverLineWidth(7)).toEqual([
			'case',
			['all', ['==', ['get', '_effortIndex'], 7], ['==', ['get', '_onSeg'], 1]],
			5,
			['==', ['get', '_effortIndex'], 7],
			3,
			['==', ['get', '_onSeg'], 1],
			4,
			2,
		]);
	});

	it('hover opacity ramps hovered+on-segment 1.0, hovered 0.4, on-segment 0.8, else 0.25', () => {
		expect(effortHoverLineOpacity(3)).toEqual([
			'case',
			['all', ['==', ['get', '_effortIndex'], 3], ['==', ['get', '_onSeg'], 1]],
			1.0,
			['==', ['get', '_effortIndex'], 3],
			0.4,
			['==', ['get', '_onSeg'], 1],
			0.8,
			0.25,
		]);
	});
});
