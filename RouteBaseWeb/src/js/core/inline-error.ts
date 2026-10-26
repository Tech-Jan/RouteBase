/**
 * inline-error.ts — validation messages that do not stop the page.
 *
 * `alert("Segment name cannot be empty")` costs a modal dismissal and the user's place in the form to
 * say something the form itself should be showing. An inline message sits next to the control that is
 * wrong, announces itself once (`role="alert"`), and disappears the moment the user fixes it — no
 * dismissal, nothing blocked.
 *
 * Two flavours, one object:
 *   • FIELD errors pass `fields` — the message clears on the next input and the field carries
 *     `aria-invalid` while it shows.
 *   • ACTION errors (nothing typed to correct: draw a shape, pick an effort) pass `autoHideMs`, so the
 *     message fades on its own instead of hanging around after the user has moved on.
 */

export interface InlineError {
	show: (message: string) => void;
	clear: () => void;
}

export interface InlineErrorOptions {
	/** 'append' (default) puts the message inside `anchor`; 'after' puts it as `anchor`'s next sibling. */
	place?: 'append' | 'after';
	/** Inputs this message is about: they clear it on edit and carry aria-invalid while it shows. */
	fields?: ReadonlyArray<HTMLElement | null | undefined>;
	/** Clear the message by itself after N ms — for errors with no field to fix. */
	autoHideMs?: number;
	/** Extra class on the message element (the base `inline-error` is always applied). */
	className?: string;
}

export function createInlineError(anchor: HTMLElement, opts: InlineErrorOptions = {}): InlineError {
	const el = document.createElement('div');
	el.className = opts.className ? `inline-error ${opts.className}` : 'inline-error';
	// polite, not assertive: a validation message should not interrupt what a screen reader is reading.
	el.setAttribute('role', 'status');
	el.hidden = true;
	if (opts.place === 'after') anchor.insertAdjacentElement('afterend', el);
	else anchor.appendChild(el);

	const fields = (opts.fields ?? []).filter((f): f is HTMLElement => !!f);
	let hideTimer: number | undefined;

	function clear(): void {
		if (hideTimer !== undefined) {
			window.clearTimeout(hideTimer);
			hideTimer = undefined;
		}
		if (el.hidden) return;
		el.hidden = true;
		el.textContent = '';
		for (const f of fields) {
			f.removeAttribute('aria-invalid');
			f.classList.remove('is-invalid');
		}
	}

	function show(message: string): void {
		if (hideTimer !== undefined) window.clearTimeout(hideTimer);
		el.textContent = message;
		el.hidden = false;
		for (const f of fields) {
			f.setAttribute('aria-invalid', 'true');
			f.classList.add('is-invalid');
		}
		hideTimer = opts.autoHideMs
			? window.setTimeout(() => { hideTimer = undefined; clear(); }, opts.autoHideMs)
			: undefined;
	}

	for (const f of fields) {
		f.addEventListener('input', clear);
		f.addEventListener('change', clear);
	}

	return { show, clear };
}
