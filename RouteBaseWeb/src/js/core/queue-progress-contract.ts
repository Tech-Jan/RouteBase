/**
 * Wire contract of GET /api/queue/progress — the GENERATED [JsonPolymorphic] union over the server's three
 * response branches (D30, closing D13). Sourced from Models/QueueContracts.cs (ProgressResponse base +
 * Idle/Basic/Import derived), drift-gated — no longer hand-modeled. Consumed by core/op-toast.ts (toast),
 * pages/import.ts (file import) and pages/keys-and-data.ts.
 *
 * TWO discriminators, two axes — do not conflate them:
 *   • `$type` (idle | basic | import) is the SHAPE discriminator, written first, REQUIRED in every branch
 *     (the transformer forces it — RequiredPropertiesSchemaTransformer D30), so this union NARROWS on it.
 *     Field access to the basic/import-only fields needs `$type` narrowing.
 *   • `operationType` (analysis | import | trackImport | heatmap | idle) is the LABEL — which toast
 *     colour/title, and which caller "owns" the op. It is `string` on the wire (not a literal union), so it
 *     does NOT narrow the type; use it only for business branching AFTER narrowing on `$type`. Note a FILE
 *     import is `$type: "import"` but `operationType: "trackImport"` — same shape, different label.
 *
 * `$type` is ADDITIVE: old clients/tabs mid-deploy keep reading `operationType` and never notice it.
 */

import type { ProgressResponse } from '../generated/contracts';

/** The GET /api/queue/progress union — narrow on `$type` (see file header). */
export type ProgressStatus = ProgressResponse;
