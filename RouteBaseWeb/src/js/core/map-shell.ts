/**
 * core/map-shell.ts — Single-engine MapLibre map lifecycle orchestrator (v3)
 *
 * MapShell handles the lifecycle plumbing shared across all map pages:
 *   • MapLibre init via the page's createMapLibre() callback, with camera preservation
 *   • Registry-driven style switching (one button per availableBasemaps())
 *   • Back-forward cache (bfcache) recovery
 *   • Map size controls + fullscreen
 *
 * Pages provide rendering callbacks; MapShell calls them at the right moment.
 * MapShell never creates MapLibre itself — the page's createMapLibre() callback
 * does that, keeping each page in full control of its MapLibre instance.
 *
 * Usage:
 *   import { createMapShell } from '../core/map-shell';
 *   import { defaultBasemapId } from '../core/basemap-registry';
 *   const shell = createMapShell({
 *       containerId: 'map',
 *       styleSwitcher: { containerId: 'basemapSwitcher' },
 *       deferInit: true,
 *       createMapLibre(containerId, styleUrl, savedCam, basemapId) { return new maplibregl.Map(...); },
 *       onMapLibreReady(map, styleUrl, savedCam) { ... },
 *   });
 *   shell.switchToBasemap(defaultBasemapId());
 */

import { setData } from '../core/data-attrs';
import type { SavedCamera, MapShellConfig, MapShellInstance } from './core-types';
import { mapSizeControls, mapFullscreen } from './map-utils-shared';
import { resolveStyleUrl, availableBasemaps, defaultBasemapId } from './basemap-registry';
import { setBuildingExtrusionsVisible, EXTRUSIONS_DEFAULT_VISIBLE } from './map-building-extrusions';
import { BuildingsControl } from './map-building-extrusions-control';

// ---------------------------------------------------------------------------
// Internal helpers
// ---------------------------------------------------------------------------

/** Clear the inner HTML of a container by id. */
function _clearContainer(containerId: string): void {
    const el = document.getElementById(containerId);
    if (el) el.innerHTML = '';
}

// ---------------------------------------------------------------------------
// Public factory
// ---------------------------------------------------------------------------

/**
 * Create a MapShell instance for the given config.
 * Call this once per page. Returns a MapShellInstance for programmatic control.
 */
export function createMapShell(config: MapShellConfig): MapShellInstance {

    // ── Internal state ──────────────────────────────────────────────────────

    let _maplibreMap: maplibregl.Map | null = null;
    let _activeEngine: 'maplibre' | null = null;
    let _lastStyleUrl: string | null = null;
    // Basemap id travels alongside the URL so pages can route DEM by id (BT-05).
    // The URL is still never persisted (invariant 3).
    let _lastBasemapId: string | null = null;
    let _styleGeneration = 0;

    // pageshow listener reference so we can remove it on destroy()
    let _bfcacheListener: ((e: PageTransitionEvent) => void) | null = null;

    // Size-button unwire (fed by _wireSizeControls), called on destroy() so a re-init can't stack listeners.
    let _sizeControlsTeardown: (() => void) | null = null;

    // Camera captured on a bfcache pageshow (before the map is destroyed) so the
    // re-init can restore the prior viewport instead of the default framing (MS-01).
    let _bfcacheCam: SavedCamera | null = null;

    // All style button elements (fed by _wireStyleSwitcher).
    const _allStyleBtns: (HTMLElement | null)[] = [];

    // Registry-driven switcher buttons, keyed by basemap id.
    const _basemapBtns: Record<string, HTMLElement> = {};

    // 3D-buildings toggle (BT-11): session-scoped, initialized from the device
    // default (desktop on, mobile off). No persistence, no window globals. The
    // current state is re-applied at every style-ready hook (style loads replace
    // the layers, wiping the last setLayoutProperty). The toggle is surfaced as a
    // native-style map control (_buildingsControl), recreated per map instance.
    let _extrusionsVisible = EXTRUSIONS_DEFAULT_VISIBLE;
    let _buildingsControl: BuildingsControl | null = null;

    // ── Style button helpers ────────────────────────────────────────────────

    function _setActive(active: HTMLElement | null): void {
        _allStyleBtns.forEach(b => { if (b) b.classList.remove('active'); });
        if (active) active.classList.add('active');
    }

    // ── Camera capture ──────────────────────────────────────────────────────

    function captureCamera(): SavedCamera | null {
        try {
            if (_activeEngine === 'maplibre' && _maplibreMap) {
                const ml = _maplibreMap;
                return {
                    center: ml.getCenter().toArray() as [number, number],
                    zoom: ml.getZoom(),
                    pitch: ml.getPitch(),
                    bearing: ml.getBearing(),
                };
            }
        } catch (e: unknown) { console.warn('[map-shell] captureCamera failed:', e); }
        return null;
    }

    // ── MapLibre destroy ────────────────────────────────────────────────────

    function _destroyMapLibre(): void {
        if (!_maplibreMap) return;
        if (config.destroyMapLibre) {
            try { config.destroyMapLibre(_maplibreMap); } catch (_e) { /* teardown */ }
        }
        _maplibreMap = null;
    }

    // ── 3D buildings (BT-11) ────────────────────────────────────────────────

    /** Re-apply the current building-extrusion visibility to the live map. */
    function _applyExtrusionState(): void {
        if (_activeEngine === 'maplibre' && _maplibreMap) {
            setBuildingExtrusionsVisible(_maplibreMap, _extrusionsVisible);
        }
    }

    /** Add the 3D-buildings toggle control to a freshly created map, next to the native nav/terrain
     *  controls. Recreated per map instance (controls die with their map on destroy/bfcache). */
    function _addBuildingsControl(map: maplibregl.Map): void {
        _buildingsControl = new BuildingsControl({
            isVisible: () => _extrusionsVisible,
            onToggle: () => { _extrusionsVisible = !_extrusionsVisible; _applyExtrusionState(); },
        });
        try { map.addControl(_buildingsControl, 'top-left'); } catch (e: unknown) {
            console.warn('[map-shell] addControl(buildings) failed:', e);
        }
    }

    // ── Engine lifecycle ────────────────────────────────────────────────────

    function switchToMapLibre(styleUrl: string, basemapId?: string | null): void {
        const nextBasemapId = basemapId ?? null;

        // Already in MapLibre → in-place style switch
        if (_activeEngine === 'maplibre' && _maplibreMap) {
            // MS-04: a same-style click never fires style.load (and setStyle early-returns
            // on _currentStyleKey) — skip it shell-side so the re-apply listener below can
            // rely on style.load always firing after a real switch.
            if (styleUrl === _lastStyleUrl && nextBasemapId === _lastBasemapId) return;

            if (config.switchMapLibreStyle) {
                try { config.switchMapLibreStyle(_maplibreMap, styleUrl, nextBasemapId); } catch (e: unknown) {
                    console.warn('[map-shell] switchMapLibreStyle failed:', e);
                    return; // MS-06: don't commit a style that never applied
                }
            }
            // MS-06: commit the new style only after the switch call returned without throwing.
            _lastStyleUrl = styleUrl;
            _lastBasemapId = nextBasemapId;
            // MS-04: re-apply extrusion visibility + refresh the buildings control once the new
            // style's layers exist. Use style.load, not idle — idle can fire during the async
            // style-JSON fetch (against the old style), consuming the callback too early (BT-11).
            try { _maplibreMap.once('style.load', () => { _applyExtrusionState(); _buildingsControl?.sync(); }); } catch (_e) { /* teardown */ }
            return;
        }

        // MS-01: on a bfcache re-init the live map is already gone, so captureCamera()
        // returns null — fall back to the camera stashed before the pageshow destroy.
        const savedCam = captureCamera() ?? _bfcacheCam;
        _bfcacheCam = null;
        const gen = ++_styleGeneration;

        // Tear down existing map
        _destroyMapLibre();
        _clearContainer(config.containerId);
        _activeEngine = null;

        // Page creates the MapLibre map
        const newMap = config.createMapLibre(config.containerId, styleUrl, savedCam, nextBasemapId);
        if (!newMap) {
            console.warn('[map-shell] createMapLibre returned null');
            return;
        }

        // MS-06: commit the new style only after createMapLibre returned a live map.
        _lastStyleUrl = styleUrl;
        _lastBasemapId = nextBasemapId;

        _maplibreMap = newMap;
        _activeEngine = 'maplibre';

        newMap.once('idle', () => {
            if (gen !== _styleGeneration) return;

            // Restore camera — only if pitch or bearing is non-zero to avoid
            // double-applying center/zoom when createMapLibre already positioned the map.
            if (savedCam && (savedCam.pitch !== 0 || savedCam.bearing !== 0)) {
                try {
                    newMap.jumpTo({
                        center: savedCam.center,
                        zoom: savedCam.zoom,
                        pitch: savedCam.pitch,
                        bearing: savedCam.bearing,
                    });
                } catch (e: unknown) { console.warn('[map-shell] jumpTo failed:', e); }
            }

            config.onMapLibreReady(newMap, styleUrl, savedCam);

            // Apply the current 3D-buildings state (initial = device default) after the fresh style's
            // layers exist (BT-11), then add the buildings toggle control beside the native controls.
            _applyExtrusionState();
            _addBuildingsControl(newMap);
        });
    }

    // ── Basemap switching (registry-driven) ─────────────────────────────────

    /** Switch to a basemap by registry id: resolve its style URL and (re)load MapLibre. */
    function switchToBasemap(id: string): void {
        const url = resolveStyleUrl(id);
        if (!url) { console.warn('[map-shell] switchToBasemap: unresolved id', id); return; }
        switchToMapLibre(url, id);
    }

    /** Mark a switcher button active by basemap id. */
    function setActiveBasemap(id: string): void {
        _setActive(_basemapBtns[id] ?? null);
    }

    // ── Style button wiring ─────────────────────────────────────────────────

    /** Registry-driven switcher: one button per availableBasemaps() into a container div. */
    function _wireStyleSwitcher(): void {
        const sw = config.styleSwitcher;
        if (!sw) return;
        const container = document.getElementById(sw.containerId);
        if (!container) return;

        // A destroyed shell's buttons stay in the container (destroy() tears down the map, not page
        // chrome) — a re-init (e.g. the live-share map Retry) would append a duplicate button set whose
        // stale handlers drive the dead shell. The container is exclusively ours, so start empty.
        container.innerHTML = '';

        const btnClass = sw.btnClass ?? 'btn btn-sm btn-outline-secondary';

        const addBtn = (id: string, label: string): void => {
            const btn = document.createElement('button');
            btn.type = 'button';
            btn.className = btnClass;
            setData(btn, 'basemapId', id);
            btn.textContent = label;
            btn.addEventListener('click', (e: Event) => {
                e.preventDefault();
                switchToBasemap(id);
                setActiveBasemap(id);
            });
            container.appendChild(btn);
            _basemapBtns[id] = btn;
            _allStyleBtns.push(btn);
        };

        availableBasemaps().forEach(b => addBtn(b.id, b.label));
        // The 3D-buildings toggle used to be appended here as a text button; it is now a native-style
        // map control (see _addBuildingsControl), so the style list holds only basemap choices.
    }

    // ── Size controls & fullscreen ──────────────────────────────────────────

    function invalidate(): void {
        setTimeout(() => {
            if (_activeEngine === 'maplibre' && _maplibreMap) {
                _maplibreMap.resize();
            }
        }, 100);
    }

    function _wireSizeControls(): void {
        const sc = config.sizeControls;
        if (!sc) return;

        _sizeControlsTeardown = mapSizeControls({
            mapDivId:      sc.mapDivId ?? config.containerId,
            growBtnId:     sc.growBtnId,
            shrinkBtnId:   sc.shrinkBtnId,
            resetBtnId:    sc.resetBtnId,
            defaultHeight: sc.defaultHeight,
            step:          sc.step,
            maxHeight:     sc.maxHeight,
            invalidate:    invalidate,
        });

        if (sc.fullscreenBtnId && sc.fullscreenCardId) {
            mapFullscreen({
                btnId:      sc.fullscreenBtnId,
                cardId:     sc.fullscreenCardId,
                invalidate: invalidate,
            });
        }
    }

    // ── Back-forward cache recovery ─────────────────────────────────────────

    function _setupBfcache(): void {
        if (config.handleBfcache === false) return;

        _bfcacheListener = (e: PageTransitionEvent): void => {
            if (!e.persisted) return;

            if (config.onBfcacheRestore) {
                try { config.onBfcacheRestore(); } catch (_e) { /* teardown */ }
            }

            const restoredStyleUrl = _lastStyleUrl;
            const restoredBasemapId = _lastBasemapId;

            if (_activeEngine === 'maplibre') {
                // MS-01: stash the live camera before destroying the map — the re-init
                // (createMapLibre) then restores this viewport instead of the default.
                _bfcacheCam = captureCamera();
                ++_styleGeneration;
                _destroyMapLibre();
                _activeEngine = null;
                _clearContainer(config.containerId);

                // Re-init with the same style + remembered basemap id
                setTimeout(() => {
                    if (restoredStyleUrl) switchToMapLibre(restoredStyleUrl, restoredBasemapId);
                }, 0);
            }
        };

        window.addEventListener('pageshow', _bfcacheListener);
    }

    // ── Destroy everything ──────────────────────────────────────────────────

    function destroy(): void {
        ++_styleGeneration; // cancel any pending MapLibre idle callbacks

        if (_bfcacheListener) {
            window.removeEventListener('pageshow', _bfcacheListener);
            _bfcacheListener = null;
        }

        // Unwire the size buttons — a re-init (live-share map Retry) rewires them against the new
        // shell; without this the listener sets stack and one click steps the height k times.
        _sizeControlsTeardown?.();
        _sizeControlsTeardown = null;

        _destroyMapLibre();

        _activeEngine = null;
        _maplibreMap = null;
        _bfcacheCam = null; // MS-01: drop any stashed bfcache camera
    }

    // ── Init ────────────────────────────────────────────────────────────────

    _wireStyleSwitcher();
    _wireSizeControls();
    _setupBfcache();

    if (!config.deferInit) {
        switchToBasemap(defaultBasemapId());
    }

    // ── Public instance ─────────────────────────────────────────────────────

    return {
        getActiveEngine:       (): 'maplibre' | null => _activeEngine,
        getMapLibreMap:        (): maplibregl.Map | null => (_activeEngine === 'maplibre' ? _maplibreMap : null),
        getStyleUrl:           (): string | null => _lastStyleUrl,
        captureCamera,
        switchToMapLibre,
        switchToBasemap,
        setActiveBasemap,
        invalidate,
        destroy,
    };
}
