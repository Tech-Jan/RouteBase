// maplibre-style-swap.ts — the one correct "swap the basemap style while 3D terrain is on" sequence.
//
// Extracted (rule of three) from three copies that had drifted: maplibre-terrain.ts (overview/details),
// segment-stats-map.ts (segments), and the live-recording engine — whose copy dropped `{ diff: false }`
// and the pre-swap terrain teardown, which killed 3D AND made the rider layers vanish on every switch.
//
// The helper owns only the universally-safe scaffolding; each engine re-adds ITS OWN layers/camera/
// terrain in the callbacks, so no engine has to inherit another's layer model.
import { helpers as H } from './maplibre-helpers';

type StyleInput = maplibregl.StyleSpecification | string;

export interface StyleSwapCallbacks {
	/**
	 * Guard checked before each deferred step; return false if a newer swap (or teardown) has
	 * superseded this one. The caller owns the generation counter (a slow style fetch from an earlier
	 * click must not clobber a newer style).
	 */
	generationOk: () => boolean;
	/**
	 * Runs on `style.load` (guarded), right after the clean rebuild — re-add the caller's own layers +
	 * restore the camera here. With `{ diff: false }` a full reload always fires this, so re-adds are
	 * deterministic (the diffing swap did not, which is why rider layers vanished on switch).
	 */
	onStyleLoad?: (map: maplibregl.Map) => void;
	/**
	 * Runs on the first `idle` after `style.load` (guarded) — re-apply 3D terrain here. `style.load`
	 * fires before the new basemap's tiles settle; terrain re-apply wants the map idle. Omit to skip
	 * the idle wait entirely.
	 */
	onIdle?: (map: maplibregl.Map) => void;
}

// Terrain ids torn down before every swap. `setStyle({ diff: false })` wipes all sources/layers anyway,
// but the ORDERED teardown (setTerrain(null) → strip → setStyle) stops the depth-render pipeline before
// setStyle destroys the shader programs it uses — without it a draw pass mid-swap can useProgram() on a
// deleted program (the draw_terrain / WebGL fault class). Both provider ids are covered.
const _TERRAIN_LAYERS = ['hillshade', 'hills'];
const _TERRAIN_SOURCES = ['terrainSource', 'hillshadeSource'];

// Pending style.load/idle listeners per map, so a rapid second switch cancels the first's callbacks
// instead of leaking a listener that fires against a superseded generation.
const _pending = new WeakMap<maplibregl.Map, { styleLoad?: (() => void) | undefined; idle?: (() => void) | undefined }>();

/** Detach any style.load/idle listeners a prior (superseded or torn-down) swap left on this map. */
export function cancelPendingSwap(map: maplibregl.Map): void {
	const p = _pending.get(map);
	if (!p) return;
	if (p.styleLoad) { try { map.off('style.load', p.styleLoad); } catch { /* teardown */ } }
	if (p.idle) { try { map.off('idle', p.idle); } catch { /* teardown */ } }
	_pending.delete(map);
}

/**
 * Swap `map` to `style` with 3D terrain on, the way the overview/segments maps do it:
 * terrain off → strip terrain sources → `setStyle(style, { diff: false })` → re-add on style.load →
 * re-apply terrain on idle. The caller passes a pre-resolved style (an inline object or a fetched
 * style JSON; a bare URL works too as a last-resort fallback) plus its own re-add callbacks.
 */
export function swapStyleWithTerrain(map: maplibregl.Map, style: StyleInput, cb: StyleSwapCallbacks): void {
	cancelPendingSwap(map); // a newer swap supersedes any in-flight one on this map

	try { map.setTerrain(null); } catch { /* terrain may already be disabled */ }
	_TERRAIN_LAYERS.forEach((id) => H.safeRemoveLayer(map, id));
	_TERRAIN_SOURCES.forEach((id) => H.safeRemoveSource(map, id));

	// Full clean rebuild — the diff engine can't reconcile terrain transitions, and a diff swap is what
	// left the live map's rider track + dot wiped with no style.load to re-add them.
	map.setStyle(style as maplibregl.StyleSpecification, { diff: false });

	const onStyleLoad = (): void => {
		const entry = _pending.get(map);
		if (entry) entry.styleLoad = undefined;
		if (!cb.generationOk()) return;

		if (cb.onStyleLoad) cb.onStyleLoad(map);

		const cbOnIdle = cb.onIdle;
		if (cbOnIdle) {
			const onIdle = (): void => {
				const e2 = _pending.get(map);
				if (e2) e2.idle = undefined;
				if (!cb.generationOk()) return;
				cbOnIdle(map);
			};
			if (entry) entry.idle = onIdle;
			else _pending.set(map, { idle: onIdle });
			map.once('idle', onIdle);
		}
	};

	_pending.set(map, { styleLoad: onStyleLoad });
	map.once('style.load', onStyleLoad);
}
