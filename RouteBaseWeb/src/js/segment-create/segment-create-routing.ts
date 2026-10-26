// segment-create-routing.ts — Snap-to-roads routing for the segment-create page.
// Engine-agnostic fetch: BRouter → OSRM fallback chain. Returns [lat, lng] tuples (the render
// layer flips to [lng, lat]). OpenRouteService + GraphHopper were dropped: both need an API key and
// were called without one, so they could only ever 401.
// Extracted verbatim from segment-create.ts (SC-01).

export interface Waypoint {
	lat: number;
	lng: number;
}

export async function fetchRoute(start: Waypoint, end: Waypoint): Promise<[number, number][] | null> {
	// 1. BRouter hiking profile
	try {
		const brouterUrl = `https://brouter.de/brouter?lonlats=${start.lng},${start.lat}|${end.lng},${end.lat}&profile=hiking&alternativeidx=0&format=geojson`;
		const response = await fetch(brouterUrl);
		const data = await response.json();
		if (data.features && data.features.length > 0 && data.features[0].geometry) {
			return data.features[0].geometry.coordinates.map(
				(c: number[]) => [c[1], c[0]] as [number, number],
			);
		}
	} catch (error) {
		console.warn("BRouter failed:", error);
	}

	// 2. OSRM foot
	try {
		const osrmUrl = `https://router.project-osrm.org/route/v1/foot/${start.lng},${start.lat};${end.lng},${end.lat}?overview=full&geometries=geojson`;
		const response = await fetch(osrmUrl);
		const data = await response.json();
		if (data.code === "Ok" && data.routes && data.routes.length > 0) {
			return data.routes[0].geometry.coordinates.map(
				(c: number[]) => [c[1], c[0]] as [number, number],
			);
		}
	} catch (error) {
		console.warn("OSRM failed:", error);
	}

	console.warn("All routing services failed");
	return null;
}
