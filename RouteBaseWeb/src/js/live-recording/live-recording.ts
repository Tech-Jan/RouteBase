// live-recording.ts — entry point for the two recording pages.
// Record page:   live-recording-record.ts   (navigator.geolocation → batched POSTs)
// LiveView page: live-recording-liveview.ts  (SignalR observer)

import { initRecordPage } from './live-recording-record';
import { initLiveViewPage } from './live-recording-liveview';
import { readIsland } from '../core/page-bootstrap';
import { setMapTilerApiKey } from '../core/map-api-key';

document.addEventListener('DOMContentLoaded', () => {
	// Initialise the MapTiler key seam before either page builds its map. LiveView emits the #mapTilerKey
	// island via _ProfileSettingsBootstrap; the Record page has no island → '' → keyless (liberty).
	setMapTilerApiKey(readIsland<string>('mapTilerKey', ''));

	if (document.getElementById('rec-active')) {
		initRecordPage(); // owns its own pagehide flush + timer teardown
	} else if (document.getElementById('liveview-app')) {
		const dispose = initLiveViewPage();
		// LV-06: LiveView had no teardown path at all; dispose() is idempotent. `persisted` guard (FE-05):
		// pagehide also fires on bfcache ENTRY, and disposing there destroys the map shell — including its
		// own pageshow recovery listener — so Back restored a blank, dead page. Only a real unload disposes.
		window.addEventListener('pagehide', (e) => { if (!e.persisted) dispose(); });
	}
});
