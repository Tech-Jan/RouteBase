// import-chunking.ts — split an upload queue into request-sized chunks. Pure, no DOM.

/**
 * Group items into chunks capped by BOTH item count and total bytes. An item bigger than
 * maxBytes on its own gets a chunk of its own (never dropped, never merged).
 */
export function chunkBySize<T>(items: T[], sizeOf: (item: T) => number, maxFiles: number, maxBytes: number): T[][] {
	const chunks: T[][] = [];
	let current: T[] = [];
	let bytes = 0;
	for (const item of items) {
		const size = sizeOf(item);
		if (current.length > 0 && (current.length >= maxFiles || bytes + size > maxBytes)) {
			chunks.push(current);
			current = [];
			bytes = 0;
		}
		current.push(item);
		bytes += size;
	}
	if (current.length) chunks.push(current);
	return chunks;
}
