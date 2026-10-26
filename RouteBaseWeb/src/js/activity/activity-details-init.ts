/**
 * Activity Details - Main Initialization Module
 * Creates the MapShell, wires all page callbacks, and bootstraps charts.
 */
import type { GpsPoint, MapShellInstance } from '../core/core-types';
import {
	initSpeedChart,
	initElevationChart,
	initHeartRateChart,
	initActPanelMgr,
	updateHighlight,
	hideHighlight,
	getSpeedChart,
	getElevationChart,
} from './activity-details';
import { initCollapsibleCards } from '../core/collapsible-card';
import { createMapShell } from '../core/map-shell';
import { defaultBasemapId } from '../core/basemap-registry';
import { setGpsData } from '../core/gps-data';
import { getCurrentColorMode, setCurrentColorMode } from '../core/map-dispatcher';
import { setLockedSplit } from '../core/split-state';
import {
	initMapLibreTerrain, setMaplibreStyle, destroyMapLibre, updateMaplibreTrackColors,
} from '../maplibre/maplibre-terrain';
import { getMaplibreMap, setMaplibreMap } from '../core/map-state';
import { getHoverTracking, getReplayButtonVisibility, closeAllActionPanels, setOnChartClick } from './details-shared';
import { registerActivityDetailsAccess } from '../track-edit/activity-access';
import { setMapTilerApiKey } from '../core/map-api-key';
import { getAccountSettings } from '../core/account-settings';

/** The #activityDetailsConfig island — the GENERATED DTO (F2a) with the columnar gpsTrack
 *  expanded client-side into per-point objects. Field docs live on ActivityDetailsConfigDto
 *  (Models/Sharing/ActivityDetailView.cs). */
export interface ActivityDetailsConfig
	extends Omit<import('../generated/contracts').ActivityDetailsConfigDto, 'gpsTrack'> {
	gpsData: GpsPoint[];
}

export function initializeActivityDetails(config: ActivityDetailsConfig): MapShellInstance {
	// Provide the editor's activity-access seam: the editor (track-edit) consumes chart lifecycle +
	// action-panel slots through the abstraction it owns, so it never imports activity internals. Runs
	// before the empty-GPS early return so entering edit mode always finds a live provider.
	registerActivityDetailsAccess({
		initSpeedChart, initElevationChart, initHeartRateChart,
		getSpeedChart, getElevationChart,
		closeAllActionPanels, setOnChartClick,
	});

	// The MapTiler key seam is initialised from the Details JSON island (this page carries the key in its
	// own config, not the shared #mapTilerKey island). Account settings are NOT set here — core/core.ts
	// fills that seam from the layout's #appSettings island before any page entry runs.
	setMapTilerApiKey(config.mapTilerApiKey);
	// Page state now lives in modules, not window.
	setGpsData(config.gpsData);
	setCurrentColorMode('none');
	setLockedSplit(null);

	const shell = createMapShell({
		containerId: 'map',
		styleSwitcher: { containerId: 'detMapStyleSwitcher', btnClass: 'det-map-btn' },
		sizeControls: {
			growBtnId:        'mapGrowBtn',
			shrinkBtnId:      'mapShrinkBtn',
			resetBtnId:       'mapResetBtn',
			fullscreenBtnId:  'mapFullscreenBtn',
			fullscreenCardId: 'mapCard',
			defaultHeight:    700,
		},
		handleBfcache: true,
		deferInit: true,

		createMapLibre(containerId, styleUrl, savedCam, basemapId) {
			const mapDiv = document.getElementById(containerId);
			if (!mapDiv) return null;
			const zoom = getAccountSettings().map.defaultZoom;
			const exaggeration = getAccountSettings().map.terrainExaggeration;
			initMapLibreTerrain(mapDiv, config.gpsData, zoom, exaggeration, styleUrl, basemapId, {
				// Read the slots at map-load time; splits/replay-ui register them at boot.
				setupHoverTracking: () => getHoverTracking()?.(),
				onTrackHover: updateHighlight,
				onTrackLeave: hideHighlight,
				onMapReady: () => getReplayButtonVisibility()?.(),
			}, savedCam); // MS-02: restore the prior viewport on bfcache re-init
			return getMaplibreMap();
		},

		onMapLibreReady(_mlMap, _styleUrl, _savedCam) {
			const colorMode = getCurrentColorMode() || 'none';
			if (colorMode !== 'none') {
				updateMaplibreTrackColors(colorMode, config.gpsData);
			}
		},

		switchMapLibreStyle(_mlMap, styleUrl, basemapId) {
			void setMaplibreStyle(styleUrl, basemapId);
		},

		destroyMapLibre(_mlMap) {
			destroyMapLibre();
			setMaplibreMap(null);
		},
	});

	if (config.gpsData.length === 0) {
		console.warn('No GPS data available');
		// DC-05: no track → the map never gets a basemap (blank card) and its style
		// buttons would still be live. Hide the map card entirely; KPI strip stays.
		document.getElementById('mapCard')?.style.setProperty('display', 'none');
		return shell;
	}

	// Charts are engine-independent — initialize once regardless of which map starts first
	initSpeedChart();
	initElevationChart();
	initHeartRateChart();
	initActPanelMgr();
	initCollapsibleCards();

	// From the seam, not config.profileSettings: the layout delivers these for every page, so the Details
	// island's settings half is a second copy of the same values. Its DTO goes away with the other four
	// delivery paths; these two reads are what kept it alive on the client.
	setCurrentColorMode(getAccountSettings().ui.defaultColorMode);

	// Resolve the default basemap id (registry decides URL + button set at runtime). Always consult
	// the profile default when it resolves, else liberty. Persist/pass ids, never URLs.
	const defaultId = defaultBasemapId(getAccountSettings().map.defaultStyle);
	shell.switchToBasemap(defaultId);
	shell.setActiveBasemap(defaultId);

	return shell;
}
