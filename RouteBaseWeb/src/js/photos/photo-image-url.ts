// photo-image-url.ts — the ONE place that builds a photo rendition URL.
//
// There were three copies of this line (the Details grid, the map overlay, the cluster popup). When the
// share view arrived only the first was rebased onto the token path, so every map thumbnail and cluster
// popup on a share page requested the authenticated endpoint and 404'd for the anonymous audience the
// page exists to serve. One builder, one base, no drift.
//
// The base is module state set once per page render, not a parameter: the marker/popup call sites sit
// several layers below whoever knows the render context, and threading it through each would reintroduce
// exactly the "one call site missed the rebase" bug this module exists to prevent.

/** Default: the authenticated endpoint. Share views override via {@link setPhotoApiBase}. */
const DEFAULT_PHOTO_API_BASE = "/api/photos";

let photoApiBase: string = DEFAULT_PHOTO_API_BASE;

/**
 * Point every photo URL at `base` for the rest of this page's life.
 *
 * On a share view the token is part of the PATH ("/s/<token>/api/photos") because an `<img>` request
 * cannot carry a header and the share slice deliberately has no cookie — so the credential has to ride
 * in the URL or the image cannot load at all.
 */
export function setPhotoApiBase(base: string): void {
	photoApiBase = base || DEFAULT_PHOTO_API_BASE;
}

/** Rendition URL for a photo. `v` (content-hash prefix) makes it content-addressed. */
export function photoImageUrl(
	p: { photoId: number; v: string },
	tier: "thumb" | "preview",
): string {
	return `${photoApiBase}/${p.photoId}/image?tier=${tier}&v=${encodeURIComponent(p.v)}`;
}
