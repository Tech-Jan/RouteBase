/**
 * xhr-upload.ts — POST a FormData with upload-progress events.
 *
 * fetch() exposes no upload progress, so byte-level staging feedback needs XMLHttpRequest.
 */

export interface UploadResponse {
	ok: boolean;
	status: number;
	statusText: string;
	body: unknown;
}

/** Rejects on network/abort (like fetch); non-2xx resolves with ok:false and the parsed JSON body. */
export function postFormWithProgress(
	url: string,
	form: FormData,
	onProgress?: (loadedBytes: number) => void,
): Promise<UploadResponse> {
	return new Promise((resolve, reject) => {
		const xhr = new XMLHttpRequest();
		xhr.open("POST", url);
		if (onProgress) xhr.upload.addEventListener("progress", (e) => onProgress(e.loaded));
		xhr.addEventListener("load", () => {
			let body: unknown = null;
			try { body = xhr.responseText ? JSON.parse(xhr.responseText) : null; } catch { /* non-JSON body */ }
			resolve({ ok: xhr.status >= 200 && xhr.status < 300, status: xhr.status, statusText: xhr.statusText, body });
		});
		xhr.addEventListener("error", () => reject(new Error("Network error")));
		xhr.addEventListener("abort", () => reject(new Error("Upload aborted")));
		xhr.send(form);
	});
}
