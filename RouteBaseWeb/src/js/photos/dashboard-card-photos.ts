// dashboard-card-photos.ts — lazy cover-photo strips on the dashboard's activity cards.
//
// Mirrors the minimap lazy-load (setupLazyThumbnails): an IntersectionObserver fetches a card's
// photos on first scroll into view, then unobserves — off-screen cards never hit the network. Reuses
// GET /api/photos?trackId=… (no new endpoint). Thumbs are decorative: the whole card is an <a> to
// Details, so a thumb click just navigates like any other click on the card.

import { readData } from '../core/data-attrs';
import { thumbHashToDataURL } from "./photo-thumbhash";
import { observeWhenSettled } from "../core/settled-observer";
import type { PhotoDto } from "../generated/contracts";

const MAX_THUMBS = 4;         // desktop renders 4; CSS caps narrower viewports to 3 / 2
const ROOT_MARGIN = "200px";  // same look-ahead as the minimap loader

// Slim projection (D4) of the generated PhotoDto fields this strip needs (see PhotoContracts.cs).
type CardPhoto = Pick<PhotoDto, 'photoId' | 'thumbHashBase64' | 'origFilename' | 'v'>;

function thumbUrl(p: CardPhoto): string {
	return `/api/photos/${p.photoId}/image?tier=thumb&v=${encodeURIComponent(p.v)}`;
}

async function fillStrip(strip: HTMLElement): Promise<void> {
	const trackId = readData(strip, 'photosTrackId');
	if (!trackId) return;

	let photos: CardPhoto[];
	try {
		const resp = await fetch(`/api/photos?trackId=${trackId}&page=1&pageSize=${MAX_THUMBS}`);
		if (!resp.ok) return;
		photos = ((await resp.json()).items ?? []) as CardPhoto[];
	} catch {
		return; // network hiccup — leave the strip empty, no retry
	}
	if (photos.length === 0) return; // no photos → strip stays empty (renders as zero-width)

	const frag = document.createDocumentFragment();
	for (const p of photos.slice(0, MAX_THUMBS)) {
		// DT-13: a malformed thumbHashBase64 throws in thumbHashToDataURL — skip that photo,
		// don't sink the whole strip (was an unhandled rejection).
		try {
			const cell = document.createElement("span");
			cell.className = "activity-card-photo";
			// ThumbHash placeholder paints instantly (inline data URI) so there's no blank flash or reflow.
			const placeholder = p.thumbHashBase64 ? thumbHashToDataURL(p.thumbHashBase64) : "";
			if (placeholder) cell.style.backgroundImage = `url(${placeholder})`;

			const img = document.createElement("img");
			img.loading = "lazy";   // second lazy layer: the JPEG bytes load only when the thumb nears the viewport
			img.decoding = "async";
			// Decorative (see header: the whole card is an <a> to Details) — a filename alt is pure
			// screen-reader noise, so the correct decorative-image treatment is an empty alt.
			img.alt = "";
			img.src = thumbUrl(p);
			img.addEventListener("load", () => img.classList.add("is-loaded"));
			cell.appendChild(img);
			frag.appendChild(cell);
		} catch {
			// bad thumbhash for this photo — skip it
		}
	}
	strip.appendChild(frag);
}

export function setupCardPhotos(): void {
	const strips = document.querySelectorAll<HTMLElement>(".activity-card-photos[data-photos-track-id]");
	if (strips.length === 0) return;

	// Fires only once the user settles on a card, not while flinging past it (P-scroll).
	observeWhenSettled({
		elements: strips,
		rootMargin: ROOT_MARGIN,
		onActivate: (el) => void fillStrip(el as HTMLElement),
	});
}
