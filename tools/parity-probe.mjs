#!/usr/bin/env node
// Differential parity probe: replays the same read-only requests against the
// legacy Python backend and the C# rewrite, then reports where the *shapes*
// differ. This is the cheapest way to find the class of bugs that unit tests
// miss — bare-array vs object wrappers, missing nested keys, string-vs-int,
// different sort order, a route that exists on one side only.
//
// Usage (both backends must read the same database and accept the same token):
//   PARITY_TOKEN=<legacy token> node tools/parity-probe.mjs
//   PARITY_TOKEN=... node tools/parity-probe.mjs --only projects
//
// Optional:
//   PY_BASE (default http://127.0.0.1:5080/api)
//   CS_BASE (default http://127.0.0.1:5090/api)
//   PARITY_IDS='{"team":"...","project":"..."}' to override the sample ids
//
// Only GET is exercised: the probe must never mutate the shared database.

import fs from 'node:fs';
import path from 'node:path';

const PY = process.env.PY_BASE ?? 'http://127.0.0.1:5080/api';
const CS = process.env.CS_BASE ?? 'http://127.0.0.1:5090/api';
const TOKEN = process.env.PARITY_TOKEN;

if (!TOKEN) {
  console.error('PARITY_TOKEN is required (a legacy Authorization token).');
  process.exit(2);
}

const ids = {
  team: '67b231b45781d7125d41e311',
  projectSet: '67b231b45781d7125d41e312',
  project: process.env.PARITY_PROJECT_ID ?? '68cd75400598c5c34b69a813',
  user: '67b231b45781d7125d41e310',
  source: '6ae367ad74d8670fb79e07f5',
  ...JSON.parse(process.env.PARITY_IDS ?? '{}'),
};

// ---- route discovery ------------------------------------------------------

const featuresDir = path.join(process.cwd(), 'src', 'Shoreline.Api', 'Features');
const routeRe = /Map(?:Get|Post|Put|Delete|Patch|Methods)\(\s*"(\/v1[^"]*)"/g;
const discovered = new Set();

for (const file of fs.readdirSync(featuresDir)) {
  if (!file.endsWith('.cs')) continue;
  const text = fs.readFileSync(path.join(featuresDir, file), 'utf8');
  // A `MapGet` is unambiguous; `MapMethods` needs a verbs check.
  const getRe = /MapGet\(\s*"(\/v1[^"]*)"/g;
  let m;
  while ((m = getRe.exec(text)) !== null) discovered.add(m[1]);
  const methodsRe = /MapMethods\(\s*"(\/v1[^"]*)"\s*,\s*new\[\]\s*\{([^}]*)\}/g;
  while ((m = methodsRe.exec(text)) !== null) {
    if (/\bGET\b/i.test(m[2])) discovered.add(m[1]);
  }
}

// Endpoints whose parameters we can meaningfully fill in.
const SEGMENT_IDS = {
  teams: ids.team,
  projects: ids.project,
  'project-sets': ids.projectSet,
  files: ids.file,
  users: ids.user,
  targets: ids.target,
  sources: ids.source,
  'term-bases': ids.termBase,
  terms: ids.term,
  invitations: ids.invitation,
  applications: ids.application,
  outputs: ids.output,
  members: ids.member,
};

function fill(template) {
  const segments = template.split('/').filter(Boolean);
  const out = [];
  let parent = '';
  for (const seg of segments) {
    if (seg.startsWith('{') || seg.startsWith(':')) {
      const value = SEGMENT_IDS[parent];
      if (!value) return null; // cannot fabricate this id — skip
      out.push(encodeURIComponent(value));
    } else {
      out.push(seg);
      parent = seg;
    }
  }
  return '/' + out.join('/');
}

const extra = [
  '/v1/site/homepage',
  '/v1/languages',
  '/v1/user/info',
  '/v1/user/teams',
  '/v1/user/projects?limit=5',
  '/v1/user/invitations',
  '/v1/user/related-applications',
  '/v1/me/notifications?limit=20',
  '/v1/me/notification-preferences',
  '/v1/me/notification-capabilities',
  '/v1/me/notifications/unread-counts',
  '/v1/me/notification-sync?limit=20',
  '/v1/admin/users?limit=5',
  '/v1/admin/teams?limit=5',
  '/v1/admin/projects?limit=5',
  '/v1/storage-pool/sources',
  '/v1/storage-pool/settings',
  '/v1/storage-pool/tasks?limit=5',
  '/v1/term-bases?limit=5',
];

const targets = [...new Set([...discovered].map(fill).filter(Boolean).concat(extra))]
  .filter((p) => !/\/v1\/(notifications\/scan|.*\/scan)/.test(p));

const only = process.argv.includes('--only')
  ? process.argv[process.argv.indexOf('--only') + 1]
  : null;
const selected = only ? targets.filter((p) => p.includes(only)) : targets;

// ---- fetch ----------------------------------------------------------------

async function get(base, urlPath) {
  const res = await fetch(base + urlPath, {
    headers: {
      Authorization: 'Bearer ' + TOKEN,
      Accept: 'application/json',
      ...(process.env.PARITY_LANG ? { 'Accept-Language': process.env.PARITY_LANG } : {}),
    },
    signal: AbortSignal.timeout(30000),
  });
  const text = await res.text();
  let body = null;
  try {
    body = text.length ? JSON.parse(text) : null;
  } catch {
    body = text.slice(0, 200);
  }
  return { status: res.status, body };
}

// ---- normalisation --------------------------------------------------------

const VOLATILE = /(^|_)(time|at|date|expires|signature|etag|revision|hash|token|cursor|password)$/i;

function normalize(value, key = '') {
  if (Array.isArray(value)) {
    // Compare the structure of the first element; keep the real length.
    return { $array: value.length, $first: value.length ? normalize(value[0]) : null };
  }
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value).sort(([a], [b]) => a.localeCompare(b))) {
      if (VOLATILE.test(k) && (typeof v === 'string' || typeof v === 'number')) {
        out[k] = `<${typeof v}>`;
      } else if (typeof v === 'string' && /^https?:/.test(v)) {
        out[k] = v.split('?')[0]; // signed-URL query strings differ by design
      } else {
        out[k] = normalize(v, k);
      }
    }
    return out;
  }
  return value;
}

function diff(py, cs, at, out) {
  const pyType = py === null ? 'null' : Array.isArray(py) ? 'array' : typeof py;
  const csType = cs === null ? 'null' : Array.isArray(cs) ? 'array' : typeof cs;

  if (pyType !== csType) {
    out.push({ kind: 'type', at, py: pyType, cs: csType });
    return;
  }

  if (pyType === 'array') {
    if (py.$array !== cs.$array) out.push({ kind: 'array-length', at, py: py.$array, cs: cs.$array });
    if (py.$first !== null && cs.$first !== null) diff(py.$first, cs.$first, at + '[]', out);
    return;
  }

  if (pyType === 'object') {
    const pyKeys = new Set(Object.keys(py));
    const csKeys = new Set(Object.keys(cs));
    for (const k of pyKeys) if (!csKeys.has(k)) out.push({ kind: 'missing', at: at + '.' + k });
    for (const k of csKeys) if (!pyKeys.has(k)) out.push({ kind: 'extra', at: at + '.' + k });
    for (const k of pyKeys) if (csKeys.has(k)) diff(py[k], cs[k], at + '.' + k, out);
    return;
  }

  if (py !== cs && typeof py !== 'string' === false && py !== '<string>') {
    // Scalar drift is often legitimate (timestamps, counters). Flag it but
    // keep it separate from structural drift.
    out.push({ kind: 'value', at, py, cs });
  }
}

// ---- run ------------------------------------------------------------------

const findings = [];
let compared = 0;
let skipped = 0;

for (const urlPath of selected) {
  const [py, cs] = await Promise.all([get(PY, urlPath), get(CS, urlPath)]);

  if (py.status === 404 && cs.status === 404) {
    skipped++;
    continue;
  }
  if (py.status !== cs.status) {
    findings.push({
      urlPath,
      kind: 'status',
      py: py.status,
      cs: cs.status,
      pyBody: typeof py.body === 'string' ? py.body.slice(0, 120) : py.body,
      csBody: typeof cs.body === 'string' ? cs.body.slice(0, 120) : cs.body,
    });
    continue;
  }
  if (py.status >= 400) {
    skipped++;
    continue;
  }

  compared++;
  const drifts = [];
  diff(normalize(py.body), normalize(cs.body), '', drifts);
  const structural = drifts.filter((d) => d.kind !== 'value');
  if (drifts.length) findings.push({ urlPath, kind: 'body', structural: structural.length, drifts });
}

const byKind = findings.reduce((acc, f) => ((acc[f.kind] = (acc[f.kind] ?? 0) + 1), acc), {});

console.log(JSON.stringify({
  targets: selected.length,
  compared,
  skipped,
  findings: findings.length,
  byKind,
  results: findings,
}, null, 2));

process.exit(findings.length ? 1 : 0);
