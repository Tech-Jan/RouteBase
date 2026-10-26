// live-recording-map.ts — LiveView map module (MapLibre-only, on MapShell).
// Thin facade over liveRecMapLibre + the follow camera. MapShell owns engine
// lifecycle, the registry style switcher, size controls and bfcache recovery.
// Exported as liveRecordingMap; consumed by live-recording-liveview.ts.

import type { MapShellConfig, MapShellInstance } from '../core/core-types';
import type { LiveRecordingMapApi } from './live-recording-types';
import { createMapShell } from '../core/map-shell';
import { defaultBasemapId } from '../core/basemap-registry';
import { CameraControls } from '../maplibre/camera-controls';
import { engageFollow, releaseFollow } from '../core/replay-follow-camera';
import { liveRecMapLibre } from './live-recording-map-maplibre';

export { FOLLOW_ZOOM } from './live-recording-map-maplibre';

let _shell: MapShellInstance | null = null;
let _camera: CameraControls | null = null;
let _followId: string | null = null;

/** The followed person's latest fix; falls back to the map centre so the idle loop never stalls. */
function _followCenter(map: maplibregl.Map): maplibregl.LngLatLike {
	const last = _followId ? liveRecMapLibre.getLastPosition(_followId) : null;
	return last ?? map.getCenter();
}

const api: LiveRecordingMapApi = {
	init(opts: { containerId: string; styleSwitcherId: string;
	             sizeControls?: MapShellConfig['sizeControls'] }): void {
		_followId = null;
		_shell = createMapShell({
			containerId: opts.containerId,
			// Both live pages host the switcher in the shared .map-dd dropdown (map-toolbar.css).
			styleSwitcher: { containerId: opts.styleSwitcherId, btnClass: 'map-dd-item' },
			sizeControls: opts.sizeControls,
			deferInit: true,
			handleBfcache: true,
			createMapLibre(containerId, styleUrl, savedCam, basemapId) {
				liveRecMapLibre.init(containerId, styleUrl, savedCam, basemapId);
				return liveRecMapLibre.getMap();
			},
			onMapLibreReady() {
				// A basemap switch can hand back a *new* map — the old CameraControls is bound to the
				// dead one, so drop it and re-follow on the live map (the engine re-adds person layers).
				_camera?.deactivate();
				_camera = null;
				if (_followId) api.setFollowTarget(_followId);
			},
			switchMapLibreStyle(_m, styleUrl, basemapId) { liveRecMapLibre.setStyle(styleUrl, basemapId); },
			destroyMapLibre() { liveRecMapLibre.destroy(); },
		});
		// No profile default is plumbed here → registry default (liberty) for keyed and
		// keyless alike (BT-07). Keyed users no longer force streets.
		const id = defaultBasemapId();
		_shell.switchToBasemap(id);
		_shell.setActiveBasemap(id);
	},

	destroy(): void {
		_camera?.deactivate();
		_camera = null;
		_followId = null;
		_shell?.destroy();
		_shell = null;
		// The engine's destroy() keeps the person store (MapShell's bfcache restore re-inits and replays
		// it). This is the PAGE teardown, so the riders' coordinate arrays go too (LV-06).
		liveRecMapLibre.clearPersons();
	},

	isReady: () => liveRecMapLibre.isReady(),

	addPerson: (id, trackColor, dotColor) => liveRecMapLibre.addPerson(id, trackColor, dotColor),

	removePerson: (id) => liveRecMapLibre.removePerson(id),

	// No panning here — while following, the CameraControls idle loop centres the followed person
	// every frame; in free mode the camera belongs to the user.
	updateTrack: (personId, coords) => liveRecMapLibre.updateTrack(personId, coords),

	flyToFirstPoint: (lat, lng) => liveRecMapLibre.flyToFirstPoint(lat, lng),

	setFollowTarget(personId: string | null, opts?: { zoom?: number }): void {
		const map = liveRecMapLibre.getMap();
		if (!map) return;
		_followId = personId;

		if (personId == null) {
			releaseFollow(map, _camera); // deactivate (which stops the idle loop) + handlers back on
			return;
		}

		_camera ??= new CameraControls(map);
		// Only centre (and sometimes zoom) named, unlike the replay pages: locking on — or switching
		// rider — must recentre WITHOUT snapping bearing/pitch, and omitting `zoom` keeps the viewer's
		// own too. An omitted seed field means "keep what the map has".
		engageFollow(map, _camera, { center: _followCenter(map), zoom: opts?.zoom });
		// No playback shell here — LiveView has no clock to pause, so it drives the idle loop itself.
		_camera.startIdleLoop(() => _followCenter(map));
	},

	getFollowTarget: () => _followId,
};

export const liveRecordingMap = api;
