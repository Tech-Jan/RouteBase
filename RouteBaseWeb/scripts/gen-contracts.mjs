// gen-contracts.mjs — regenerate the TypeScript API contracts from the built OpenAPI document.
//
// Pipeline (source of truth = the C# DTOs):
//   dotnet build  ──►  openapi/RouteBaseWeb.json  ──►  [this script]  ──►  src/js/generated/
//                      (ApiDescription.Server)          openapi-typescript + flat aliases
//
// Outputs (both checked in, both regenerated here — the drift-gate diffs them):
//   src/js/generated/openapi-types.ts   raw openapi-typescript output (paths/components/operations)
//   src/js/generated/contracts.ts       flat `export type SegmentSummaryDto = components['schemas'][...]`
//                                        aliases so every DTO stays grep-able by its C# name.
//
// This does NOT run the build — it consumes whatever openapi/RouteBaseWeb.json is on disk. The drift-gate
// (scripts/check-contracts-drift.sh) runs the build first, then this, then `git diff --exit-code`.

import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, mkdirSync, existsSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const projectDir = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const DOC = resolve(projectDir, 'openapi/RouteBaseWeb.json');
const OUT_DIR = resolve(projectDir, 'src/js/generated');
const RAW = resolve(OUT_DIR, 'openapi-types.ts');
const ALIASES = resolve(OUT_DIR, 'contracts.ts');

if (!existsSync(DOC)) {
  console.error(`[gen-contracts] ${DOC} not found. Run \`dotnet build\` first (it emits the OpenAPI doc).`);
  process.exit(1);
}

mkdirSync(OUT_DIR, { recursive: true });

// 0) Request-aware relaxation (D26, closing D10): a schema referenced ONLY from request bodies gets its
//    `required` relaxed to STJ's DESERIALIZE rule — a request may omit anything that is not C# `required`/
//    [JsonRequired] (the transformer stamps that set as x-deserialize-required; absent = empty). Response
//    and dual-use schemas keep the D1 all-written rule — relaxing those would lie about what the server
//    sends. The checked-in doc is untouched; the relaxed copy is fed to openapi-typescript from tmp.
const rawDoc = JSON.parse(readFileSync(DOC, 'utf8'));
const relaxed = relaxRequestOnlySchemas(rawDoc);

// 1) raw types via openapi-typescript over the relaxed doc.
const TMP_DOC = join(tmpdir(), `routebase-openapi-relaxed-${process.pid}.json`);
writeFileSync(TMP_DOC, JSON.stringify(relaxed.doc));
const bin = resolve(projectDir, 'node_modules/.bin/openapi-typescript');
try {
  execFileSync(bin, [TMP_DOC, '-o', RAW], { cwd: projectDir, stdio: 'inherit' });
} finally {
  rmSync(TMP_DOC, { force: true });
}
console.log(`[gen-contracts] request-only schemas relaxed to deserialize-required: ${relaxed.names.join(', ') || '(none)'}`);

/** Collect `$ref: #/components/schemas/X` names reachable from a schema/content node. */
function collectRefs(node, into) {
  if (!node || typeof node !== 'object') return;
  if (typeof node.$ref === 'string') {
    const m = node.$ref.match(/^#\/components\/schemas\/(.+)$/);
    if (m) into.add(m[1]);
  }
  for (const v of Object.values(node)) collectRefs(v, into);
}

function relaxRequestOnlySchemas(doc) {
  const schemas = doc.components?.schemas ?? {};
  const reqRoots = new Set();
  const resRoots = new Set();
  for (const item of Object.values(doc.paths ?? {})) {
    for (const op of Object.values(item)) {
      if (!op || typeof op !== 'object') continue;
      collectRefs(op.requestBody, reqRoots);
      collectRefs(op.responses, resRoots);
      // Parameter schemas are INBOUND (client → server), so they relax like request bodies. This
      // mattered from E1 on: GridFilterCollection reaches the doc only via the /api/statistics/*
      // query parameters, and classifying those as response roots un-relaxed ColumnFilter — making
      // the hand-built filter maps fail tsc against all-required keys the server never demands.
      collectRefs(op.parameters, reqRoots);
    }
  }
  // Closure over component references: a child of a request root is request-reachable, etc.
  const close = (roots) => {
    const seen = new Set(roots);
    const queue = [...roots];
    while (queue.length) {
      const found = new Set();
      collectRefs(schemas[queue.pop()], found);
      for (const n of found) if (!seen.has(n)) { seen.add(n); queue.push(n); }
    }
    return seen;
  };
  const reqReachable = close(reqRoots);
  const resReachable = close(resRoots);

  const names = [];
  for (const [name, schema] of Object.entries(schemas)) {
    const requestOnly = reqReachable.has(name) && !resReachable.has(name);
    if (requestOnly && Array.isArray(schema.required)) {
      const keep = new Set(schema['x-deserialize-required'] ?? []);
      const relaxedRequired = schema.required.filter((p) => keep.has(p));
      if (relaxedRequired.length !== schema.required.length) names.push(name);
      if (relaxedRequired.length) schema.required = relaxedRequired;
      else delete schema.required;
    }
    delete schema['x-deserialize-required']; // stamp is build metadata, not client contract
  }
  return { doc, names: names.sort() };
}

// 2) flat aliases, alphabetical for a deterministic diff. One `export type <Name>` per component schema so
//    a DTO is found by grepping its C# name — not by `components['schemas'][...]` index chains.
const doc = JSON.parse(readFileSync(DOC, 'utf8'));
const schemas = Object.keys(doc.components?.schemas ?? {}).sort();

const header = `// AUTO-GENERATED by \`npm run gen:contracts\` from openapi/RouteBaseWeb.json — DO NOT EDIT.
// The C# DTOs are the source of truth; a drift here fails CI (scripts/check-contracts-drift.sh).
// Flat aliases over ./openapi-types.ts so every server contract is grep-able by its C# type name.
`;

const body = schemas.length
  ? `import type { components } from './openapi-types';\n\n` +
    schemas.map((name) => `export type ${name} = components['schemas']['${name}'];`).join('\n') + '\n'
  : '// (no component schemas yet — annotate endpoints with [ProducesResponseType<T>] to populate this)\nexport {};\n';

writeFileSync(ALIASES, header + '\n' + body);
console.log(`[gen-contracts] wrote ${schemas.length} contract aliases → src/js/generated/contracts.ts`);
