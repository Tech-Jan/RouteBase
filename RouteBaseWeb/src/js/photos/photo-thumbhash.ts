// ThumbHash decode — renders a compact (~20-byte) hash to a tiny blurred image for instant blur-up
// placeholders (§12). Faithful port of Evan Wallace's public-domain reference (github.com/evanw/thumbhash,
// thumbHashToRGBA); the encode side lives in C# (Services/Photos/ThumbHash.cs).

interface DecodedThumbHash {
	w: number;
	h: number;
	rgba: Uint8Array;
}

function thumbHashToRGBA(hash: Uint8Array): DecodedThumbHash {
	const { PI, min, max, cos, round } = Math;

	// Bucket-C port shims (noUncheckedIndexedAccess): byte() for hash reads — `undefined | x` was ToInt32(undefined)=0,
	// so `?? 0` is bit-identical in these bitwise contexts. at() for the DCT tables — those indices
	// mirror the fill loops exactly, so the fallback is unreachable; chosen over a throwing helper to
	// keep the reference port line-for-line recognizable.
	const byte = (i: number): number => hash[i] ?? 0;
	const at = (a: number[], i: number): number => a[i] ?? 0;

	// Read the constants.
	const header24 = byte(0) | (byte(1) << 8) | (byte(2) << 16);
	const header16 = byte(3) | (byte(4) << 8);
	const lDc = (header24 & 63) / 63;
	const pDc = ((header24 >> 6) & 63) / 31.5 - 1;
	const qDc = ((header24 >> 12) & 63) / 31.5 - 1;
	const lScale = ((header24 >> 18) & 31) / 31;
	const hasAlpha = header24 >> 23;
	const pScale = ((header16 >> 3) & 63) / 63;
	const qScale = ((header16 >> 9) & 63) / 63;
	const isLandscape = header16 >> 15;
	const lx = max(3, isLandscape ? (hasAlpha ? 5 : 7) : header16 & 7);
	const ly = max(3, isLandscape ? header16 & 7 : hasAlpha ? 5 : 7);
	const aDc = hasAlpha ? (byte(5) & 15) / 15 : 1;
	const aScale = (byte(5) >> 4) / 15;

	// Read the varying factors (boost saturation to compensate for quantization).
	let acIndex = hasAlpha ? 6 : 5;
	let acBit = 0;
	const decodeChannel = (nx: number, ny: number, scale: number): number[] => {
		const ac: number[] = [];
		for (let cy = 0; cy < ny; cy++) {
			for (let cx = cy ? 0 : 1; cx * ny < nx * (ny - cy); cx++) {
				ac.push((((byte(acIndex) >> acBit) & 15) / 7.5 - 1) * scale);
				if ((acBit += 4) === 8) {
					acBit = 0;
					acIndex++;
				}
			}
		}
		return ac;
	};
	const lAc = decodeChannel(lx, ly, lScale);
	const pAc = decodeChannel(3, 3, pScale * 1.25);
	const qAc = decodeChannel(3, 3, qScale * 1.25);
	const aAc = hasAlpha ? decodeChannel(5, 5, aScale) : [];

	// Decode using the DCT into RGB.
	const ratio = lx / ly;
	const w = round(ratio > 1 ? 32 : 32 * ratio);
	const h = round(ratio > 1 ? 32 / ratio : 32);
	const rgba = new Uint8Array(w * h * 4);
	const fx: number[] = [];
	const fy: number[] = [];

	for (let y = 0, i = 0; y < h; y++) {
		for (let x = 0; x < w; x++, i += 4) {
			let l = lDc;
			let p = pDc;
			let q = qDc;
			let a = aDc;

			for (let cx = 0, n = max(lx, hasAlpha ? 5 : 3); cx < n; cx++)
				fx[cx] = cos((PI / w) * (x + 0.5) * cx);
			for (let cy = 0, n = max(ly, hasAlpha ? 5 : 3); cy < n; cy++)
				fy[cy] = cos((PI / h) * (y + 0.5) * cy);

			for (let cy = 0, j = 0; cy < ly; cy++)
				for (let cx = cy ? 0 : 1; cx * ly < lx * (ly - cy); cx++, j++)
					l += at(lAc, j) * at(fx, cx) * at(fy, cy) * 2;

			for (let cy = 0, j = 0; cy < 3; cy++)
				for (let cx = cy ? 0 : 1; cx < 3 - cy; cx++, j++) {
					const f = at(fx, cx) * at(fy, cy) * 2;
					p += at(pAc, j) * f;
					q += at(qAc, j) * f;
				}

			if (hasAlpha)
				for (let cy = 0, j = 0; cy < 5; cy++)
					for (let cx = cy ? 0 : 1; cx < 5 - cy; cx++, j++)
						a += at(aAc, j) * at(fx, cx) * at(fy, cy) * 2;

			const b = l - (2 / 3) * p;
			const r = (3 * l - b + q) / 2;
			const g = r - q;
			rgba[i] = max(0, 255 * min(1, r));
			rgba[i + 1] = max(0, 255 * min(1, g));
			rgba[i + 2] = max(0, 255 * min(1, b));
			rgba[i + 3] = max(0, 255 * min(1, a));
		}
	}
	return { w, h, rgba };
}

function base64ToBytes(b64: string): Uint8Array {
	const bin = atob(b64);
	const bytes = new Uint8Array(bin.length);
	for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
	return bytes;
}

/** Decode a base64 ThumbHash to a data URL suitable for a CSS `background-image` blur-up placeholder. */
export function thumbHashToDataURL(base64: string): string {
	try {
		const { w, h, rgba } = thumbHashToRGBA(base64ToBytes(base64));
		const canvas = document.createElement("canvas");
		canvas.width = w;
		canvas.height = h;
		const ctx = canvas.getContext("2d");
		if (!ctx) return "";
		const image = ctx.createImageData(w, h);
		image.data.set(rgba);
		ctx.putImageData(image, 0, 0);
		return canvas.toDataURL();
	} catch {
		return ""; // a bad hash must never break the grid — the real image still loads
	}
}
