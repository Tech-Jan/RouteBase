// core/map-building-extrusions-control.ts — 3D buildings toggle as a MapLibre control.
//
// A native-looking custom IControl (same markup as NavigationControl / TerrainControl) so it stacks
// with them in the map's control corner on every map. MapShell adds one per map instance and owns the
// on/off state; this control is just the button. It hides itself on styles with no extrusion layers
// (see hasBuildingExtrusions) so it only appears where 3D buildings are actually possible.
// Moved here from maplibre/ with its sibling in ARCH-F1 (see map-building-extrusions.ts).

import { hasBuildingExtrusions } from './map-building-extrusions';

type IControl = import('maplibre-gl').IControl;

interface BuildingsControlOpts {
    /** Current on/off state (owned by MapShell). */
    isVisible: () => boolean;
    /** Flip the state + re-apply it to the live map (MapShell). */
    onToggle: () => void;
}

// Buildings glyph, dark to match MapLibre's built-in control icons. Encoded at runtime.
const ICON_SVG =
    "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 16 16' width='20' height='20' fill='#333'>" +
    "<path d='M6 1.5h6a.5.5 0 0 1 .5.5v12h-2v-2H7.5v2H5.5V2a.5.5 0 0 1 .5-.5zM8 4.5h1v-1H8v1zm2.5 0h1v-1h-1v1zM8 7h1V6H8v1zm2.5 0h1V6h-1v1zM8 9.5h1v-1H8v1zm2.5 0h1v-1h-1v1z'/>" +
    "<path d='M2.5 6.5H5V14H2a.5.5 0 0 1-.5-.5V7a.5.5 0 0 1 .5-.5h.5zm.5 3.5h1V9H3v1zm0 2.5h1v-1H3v1z'/>" +
    "</svg>";

export class BuildingsControl implements IControl {
    private _map?: maplibregl.Map | undefined;
    private _container?: HTMLDivElement | undefined;
    private _btn?: HTMLButtonElement | undefined;

    constructor(private readonly opts: BuildingsControlOpts) {}

    onAdd(map: maplibregl.Map): HTMLElement {
        this._map = map;

        const container = document.createElement('div');
        container.className = 'maplibregl-ctrl maplibregl-ctrl-group';

        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'maplibregl-ctrl-buildings';
        btn.addEventListener('click', () => { this.opts.onToggle(); this.sync(); });

        const icon = document.createElement('span');
        icon.className = 'maplibregl-ctrl-icon';
        icon.setAttribute('aria-hidden', 'true');
        icon.style.backgroundImage = `url("data:image/svg+xml,${encodeURIComponent(ICON_SVG)}")`;
        btn.appendChild(icon);

        container.appendChild(btn);
        this._container = container;
        this._btn = btn;
        this.sync();
        return container;
    }

    onRemove(): void {
        this._container?.parentNode?.removeChild(this._container);
        this._map = undefined;
        this._container = undefined;
        this._btn = undefined;
    }

    /** Reflect on/off state and hide the whole control on styles without building extrusions.
     *  Call after every style load (support + visibility both change with the style). */
    sync(): void {
        if (!this._map || !this._container || !this._btn) return;
        const supported = hasBuildingExtrusions(this._map);
        this._container.style.display = supported ? '' : 'none';
        const on = this.opts.isVisible();
        this._btn.classList.toggle('maplibregl-ctrl-buildings-enabled', on);
        // Self-contained "enabled" highlight (no dependency on app CSS being loaded on the map page).
        this._btn.style.backgroundColor = on ? 'rgb(215, 235, 255)' : '';
        this._btn.title = on ? 'Hide 3D buildings' : 'Show 3D buildings';
        this._btn.setAttribute('aria-label', this._btn.title);
    }
}
