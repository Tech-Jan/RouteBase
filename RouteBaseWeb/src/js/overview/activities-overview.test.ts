import { describe, it, expect, vi, beforeEach, afterEach, type Mock } from 'vitest';

// OV-07b facade pins for the rewritten (MapLibre-only) overview orchestrator. The MapLibre
// engine, the GPS loader and the progressive module are mocked; createMapShell is real
// (jsdom-safe — no map-engine global present) so the registry switcher renders and
// switchToBasemap resolves real style URLs. MAPTILER_API_KEY unset → keyless registry.
// Each test re-imports the module fresh (resetModules) and drives it through its exported
// facade (startActivitiesMap / setActivitiesFilter / setOverviewHighlight).

vi.mock('./activities-overview-maplibre', () => ({
    init: vi.fn(() => ({ once: () => {}, remove: () => {} })),
    refresh: vi.fn(),
    setTrackHighlight: vi.fn(),
    attachViewListeners: vi.fn(),
    switchStyle: vi.fn(),
    destroy: vi.fn(),
    getMap: vi.fn(() => null),
    getStyleUrl: vi.fn(() => null),
}));

vi.mock('./activities-gps-loader', () => ({
    fetchActivityViewBounds: vi.fn().mockResolvedValue({ minLat: 1, maxLat: 3, minLng: 10, maxLng: 12 }),
    loadActivitiesChunked: vi.fn(async (opts: { ids: number[]; onChunk: (t: unknown[]) => void }) => {
        opts.onChunk(opts.ids.map((id) => ({
            id, name: 'Run ' + id, date: '2026', type: 'Run', distance: 1, ascent: 10,
            points: [[1, 10], [2, 11]],
        })));
    }),
}));

vi.mock('./activities-overview-progressive', () => ({
    onViewChanged: vi.fn(),
    reset: vi.fn(),
    init: vi.fn(),
    mergeUpgradedTracks: vi.fn((a: unknown) => a),
}));

const flush = (): Promise<void> => new Promise((r) => setTimeout(r, 0));
const BOUNDS = { minLat: 1, maxLat: 3, minLng: 10, maxLng: 12 };
const LIBERTY_URL = 'https://tiles.openfreemap.org/styles/liberty';
const POSITRON_URL = 'https://tiles.openfreemap.org/styles/positron';

let mlMock: typeof import('./activities-overview-maplibre');
let gpsMock: typeof import('./activities-gps-loader');
let overview: typeof import('./activities-overview');

beforeEach(async () => {
    vi.resetModules();
    document.body.innerHTML =
        '<div id="activitiesMap"></div><div id="actMapStyleSwitcher"></div>' +
        '<span id="mapStatus"></span><div id="mapLoadingSpinner"></div>';
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    delete (window as any).MAPTILER_API_KEY;
    mlMock = await import('./activities-overview-maplibre');
    gpsMock = await import('./activities-gps-loader');
    overview = await import('./activities-overview'); // exports the map facade
});

afterEach(() => {
    document.body.innerHTML = '';
});

describe('activities overview facade — OV-07 pins', () => {
    it('startActivitiesMap keyless renders registry switcher and defaults to liberty', async () => {
        overview.startActivitiesMap([1, 2], 'liberty');
        await flush();
        await flush();

        // Basemap buttons only ([data-basemap-id]) — excludes the BT-11 "3D buildings" toggle
        // MapShell appends to the same container.
        const btns = Array.from(document.querySelectorAll<HTMLButtonElement>('#actMapStyleSwitcher button[data-basemap-id]'));
        expect(btns.map(b => b.textContent)).toEqual(['Liberty', 'Bright', 'Muted', 'OSM', 'Satellite']);

        const active = document.querySelector<HTMLButtonElement>('#actMapStyleSwitcher button.active');
        expect(active?.dataset.basemapId).toBe('liberty');

        // Engine created with the resolved liberty URL + the fetched up-front bounds + basemap id.
        expect(mlMock.init).toHaveBeenCalledWith([], LIBERTY_URL, null, expect.any(Array), BOUNDS, 'liberty');
    });

    it('startActivitiesMap honors the passed default basemap id', async () => {
        overview.startActivitiesMap([1], 'positron');
        await flush();
        await flush();

        expect(mlMock.init).toHaveBeenCalledWith([], POSITRON_URL, null, expect.any(Array), BOUNDS, 'positron');
        const active = document.querySelector<HTMLButtonElement>('#actMapStyleSwitcher button.active');
        expect(active?.dataset.basemapId).toBe('positron');
    });

    it('startActivitiesMap without a default falls back to the registry default (BT-07)', async () => {
        overview.startActivitiesMap([1]); // no profile default passed
        await flush();
        await flush();

        expect(mlMock.init).toHaveBeenCalledWith([], LIBERTY_URL, null, expect.any(Array), BOUNDS, 'liberty');
        const active = document.querySelector<HTMLButtonElement>('#actMapStyleSwitcher button.active');
        expect(active?.dataset.basemapId).toBe('liberty');
    });

    it('setOverviewHighlight: paint wins over grid, null falls through', () => {
        const spy = mlMock.setTrackHighlight as ReturnType<typeof vi.fn>;
        overview.setOverviewHighlight('grid', [1]);
        expect(spy).toHaveBeenLastCalledWith([1]);
        overview.setOverviewHighlight('paint', [2]);
        expect(spy).toHaveBeenLastCalledWith([2]);
        overview.setOverviewHighlight('paint', null); // paint releases → grid shows through
        expect(spy).toHaveBeenLastCalledWith([1]);
        overview.setOverviewHighlight('grid', null);  // both inactive → clear
        expect(spy).toHaveBeenLastCalledWith(null);
    });

    // Intent holds, contract changed: the compositor gained a top-priority 'pick' source (editor picks).
    it('setOverviewHighlight: pick wins over paint+grid, null falls through', () => {
        const spy = mlMock.setTrackHighlight as ReturnType<typeof vi.fn>;
        overview.setOverviewHighlight('grid', [1]);
        overview.setOverviewHighlight('paint', [2]);
        overview.setOverviewHighlight('pick', [9]);   // checking a track → picks own the map
        expect(spy).toHaveBeenLastCalledWith([9]);
        overview.setOverviewHighlight('pick', null);  // uncheck all → paint shows through again
        expect(spy).toHaveBeenLastCalledWith([2]);
    });

    it('setActivitiesFilter redraws cached subset and delta-fetches missing ids', async () => {
        overview.startActivitiesMap([1], 'liberty'); // seeds the GPS cache with track 1
        await flush();
        await flush();
        await flush();

        const refresh = mlMock.refresh as ReturnType<typeof vi.fn>;
        const chunked = gpsMock.loadActivitiesChunked as unknown as Mock<(opts: { onChunk: (t: unknown[]) => void }) => Promise<unknown>>;
        refresh.mockClear();
        chunked.mockClear();

        overview.setActivitiesFilter([1, 99]);
        await flush();

        // Cached track 1 redrawn immediately from the in-memory GPS cache (first refresh).
        const redrawn = refresh.mock.calls[0]?.[0] as Array<{ id: number }>;
        expect(redrawn.map(t => t.id)).toEqual([1]);

        // Only the id missing from the cache is delta-fetched.
        expect(chunked).toHaveBeenCalledWith(expect.objectContaining({ ids: [99] }));
    });

    it('filter to a fully cached set hides the spinner left by a superseded load', async () => {
        overview.startActivitiesMap([1], 'liberty'); // seeds the GPS cache with track 1
        await flush();
        await flush();
        await flush();

        const spinner = document.getElementById('mapLoadingSpinner')!;
        const status = document.getElementById('mapStatus')!;
        const chunked = gpsMock.loadActivitiesChunked as unknown as Mock<(opts: { onChunk: (t: unknown[]) => void }) => Promise<unknown>>;
        chunked.mockImplementation(() => new Promise(() => {})); // never settles → finally never runs

        overview.setActivitiesFilter([1, 99]); // 99 missing → in-flight delta fetch shows the spinner
        expect(spinner.style.display).toBe('block');

        overview.setActivitiesFilter([1]); // fully cached → early-return branch owns the run
        expect(spinner.style.display).toBe('none');
        expect(status.textContent).toBe('(1 tracks)');
    });

    it('zero-track load leaves the map container intact and reports no GPS data', async () => {
        const chunked = gpsMock.loadActivitiesChunked as unknown as Mock<(opts: { onChunk: (t: unknown[]) => void }) => Promise<unknown>>;
        chunked.mockImplementation(async (opts: { onChunk: (t: unknown[]) => void }) => { opts.onChunk([]); });

        overview.startActivitiesMap([1], 'liberty');
        await flush();
        await flush();
        await flush();

        const mapDiv = document.getElementById('activitiesMap')!;
        expect(mapDiv.innerHTML).not.toContain('act-map-message');
        expect(document.getElementById('mapStatus')!.textContent).toBe('(No GPS data)');
    });
});
