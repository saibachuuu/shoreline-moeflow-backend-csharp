#!/usr/bin/env node
// Replay the same mutations against two independent copies of the production
// dump — one served by the legacy Python app, one by the C# rewrite — and
// compare what each side reports back.
//
// Why two databases: the probe in parity-probe.mjs can only read, because it
// points at the live copy. Here each backend owns its own restore of the same
// dump, so writes are safe. Every created id is captured per side and fed back
// into later steps through {{alias}} placeholders, which keeps the two runs
// symmetric even though the generated ObjectIds differ.
//
// Usage:
//   PARITY_TOKEN=<legacy token> node tools/replay-parity.mjs
//   PARITY_TOKEN=... node tools/replay-parity.mjs --flow teams
//
// Environment:
//   PY_BASE (default http://127.0.0.1:5082)
//   CS_BASE (default http://127.0.0.1:5092)
//   PARITY_TEAM_ROLE   — team_role._id for m_o = "member"
//   PARITY_PROJECT_ROLE, PARITY_JA_LANGUAGE, PARITY_PROJECT_ID as needed

import fs from 'node:fs';

const PY_BASE = process.env.PY_BASE ?? 'http://127.0.0.1:5082';
const CS_BASE = process.env.CS_BASE ?? 'http://127.0.0.1:5092';
const TOKEN = process.env.PARITY_TOKEN;
const STAMP = process.env.PARITY_STAMP ?? String(Date.now());

if (!TOKEN) {
  console.error('PARITY_TOKEN is required.');
  process.exit(2);
}

const cfg = {
  teamRole: process.env.PARITY_TEAM_ROLE ?? '67b231b35781d7125d41e29c',
  projectRole: process.env.PARITY_PROJECT_ROLE ?? '67b231b35781d7125d41e2a2',
  jaLanguage: process.env.PARITY_JA_LANGUAGE ?? '67b231b35781d7125d41e2a5',
  project: process.env.PARITY_PROJECT_ID ?? '6ae3ae62f54dd6b6f8b26bfb',
  projectSet: process.env.PARITY_PROJECT_SET_ID ?? '67b231b45781d7125d41e312',
  stranger: process.env.PARITY_STRANGER_ID ?? '6ae457e3f54dd6b6f8b26ea7',
  team: process.env.PARITY_TEAM_ID ?? '67b231b45781d7125d41e311',
};

const sides = {
  py: { name: 'py', base: PY_BASE, ids: {} },
  cs: { name: 'cs', base: CS_BASE, ids: {} },
};

// ---- flows ----------------------------------------------------------------
// [method, path, body, capture]  — capture maps alias -> "a.b.c" in the response.

const flows = {
  teams: [
    ['POST', '/v1/teams', {
      name: 'pt' + STAMP.slice(-6),
      intro: '',
      allow_apply_type: 2,
      application_check_type: 2,
      default_role: cfg.teamRole,
    }, { team: 'team.id' }],
    ['GET', '/v1/teams/{{team}}', null],
    ['PUT', '/v1/teams/{{team}}', { name: 'pt_r_' + STAMP.slice(-6), intro: 'renamed' }],
    ['GET', '/v1/teams/{{team}}', null],
    ['GET', '/v1/teams/{{team}}/members', null],
    ['GET', '/v1/teams/{{team}}/project-sets', null],
  ],

  projects: [
    ['POST', '/v1/teams/' + cfg.team + '/projects', {
      name: 'parity_project_' + STAMP,
      intro: '',
      // 3 = ProjectAllowApplyType.TEAM_USER; the project-level enum is wider
      // than the team-level one.
      allow_apply_type: 3,
      application_check_type: 2,
      default_role: cfg.projectRole,
      project_set: cfg.projectSet,
      source_language: 'ja',
      target_languages: ['zh-CN'],
    }, { project: 'project.id' }],
    ['GET', '/v1/projects/{{project}}', null],
    ['GET', '/v1/teams/' + cfg.team + '/projects', null],
    ['PUT', '/v1/projects/{{project}}', { name: 'pp_r_' + STAMP.slice(-6), intro: 'changed' }],
    ['GET', '/v1/projects/{{project}}', null],
    ['GET', '/v1/projects/{{project}}/members', null],
    ['GET', '/v1/projects/{{project}}/targets', null],
    ['GET', '/v1/projects/{{project}}/files', null],
  ],

  files: [
    ['POST', '/v1/projects/' + cfg.project + '/files', {
      $form: {
        name: 'parity_upload.png',
        type: 'image/png',
        content: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        fields: {},
      },
    }, { file: 'id' }],
    ['GET', '/v1/files/{{file}}', null],
    ['GET', '/v1/projects/' + cfg.project + '/files', null],
    ['PUT', '/v1/files/{{file}}', { name: 'parity_renamed.png' }],
    ['GET', '/v1/files/{{file}}', null],
    ['DELETE', '/v1/files/{{file}}', null],
    ['GET', '/v1/projects/' + cfg.project + '/files', null],
  ],

  labels: [
    ['POST', '/v1/projects/' + cfg.project + '/files', {
      $form: {
        name: 'parity_label.png',
        type: 'image/png',
        content: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        fields: {},
      },
    }, { lfile: 'id' }],
    ['GET', '/v1/projects/' + cfg.project + '/targets', null, { ltarget: '0.id' }],
    ['POST', '/v1/files/{{lfile}}/sources', {
      content: 'こんにちは', x: 0.1, y: 0.2, position_type: 1,
    }, { lsource: 'id' }],
    ['GET', '/v1/files/{{lfile}}/sources?target_id={{ltarget}}', null],
    ['PUT', '/v1/sources/{{lsource}}', { content: 'こんばんは' }],
    ['POST', '/v1/sources/{{lsource}}/translations', {
      content: '你好', target_id: '{{ltarget}}',
    }],
    ['GET', '/v1/files/{{lfile}}/sources?target_id={{ltarget}}', null],
    ['POST', '/v1/files/{{lfile}}/sources', {
      content: 'さようなら', x: 0.3, y: 0.4, position_type: 1,
    }, { lsource2: 'id' }],
    ['PUT', '/v1/sources/{{lsource2}}/rank', { next_source_id: '{{lsource}}' }],
    ['GET', '/v1/files/{{lfile}}/sources?target_id={{ltarget}}', null],
    ['DELETE', '/v1/sources/{{lsource2}}', null],
    ['DELETE', '/v1/sources/{{lsource}}', null],
    ['GET', '/v1/files/{{lfile}}/sources?target_id={{ltarget}}', null],
    ['DELETE', '/v1/files/{{lfile}}', null],
  ],

  exports: [
    ['GET', '/v1/projects/' + cfg.project + '/targets', null, { etarget: '0.id' }],
    ['POST', '/v1/projects/' + cfg.project + '/targets/{{etarget}}/outputs', {
      type: 1,
    }],
    ['GET', '/v1/projects/' + cfg.project + '/targets/{{etarget}}/outputs', null],
  ],

  teamMembers: [
    ['POST', '/v1/teams', {
      name: 'tm' + STAMP.slice(-6),
      intro: '',
      allow_apply_type: 2,
      application_check_type: 2,
      default_role: cfg.teamRole,
    }, { tteam: 'team.id' }],
    ['POST', '/v1/teams/{{tteam}}/members', {
      user_id: cfg.stranger,
      base_tag: 'member',
    }, { tmember: 'member.id' }],
    ['GET', '/v1/teams/{{tteam}}/members', null],
    ['PATCH', '/v1/teams/{{tteam}}/members/{{tmember}}', { expected_version: 0, base_tag: 'admin' }],
    ['GET', '/v1/teams/{{tteam}}/members', null],
    ['PATCH', '/v1/teams/{{tteam}}/members/{{tmember}}/aliases', { expected_version: 1, aliases: ['pn'] }],
    ['PATCH', '/v1/teams/{{tteam}}/members/{{tmember}}/default-display-name', { expected_version: 2, default_display_name: 'pn' }],
    ['GET', '/v1/teams/{{tteam}}/members', null],
    ['DELETE', '/v1/teams/{{tteam}}/members/{{tmember}}', null],
    ['GET', '/v1/teams/{{tteam}}/members', null],
  ],

  terms: [
    ['POST', '/v1/teams/' + cfg.team + '/term-banks', {
      name: 'tb' + STAMP.slice(-6),
      tip: '',
      source_language_id: '67b231b35781d7125d41e2a5',
      target_language_id: '67b231b35781d7125d41e2a8',
    }],
    ['GET', '/v1/teams/' + cfg.team + '/term-banks', null, { tbank: '0.id' }],
    ['POST', '/v1/term-banks/{{tbank}}/terms', {
      source: 'こんにちは', target: '你好', tip: '',
    }],
    ['GET', '/v1/term-banks/{{tbank}}/terms', null, { tterm: '0.id' }],
    ['PUT', '/v1/terms/{{tterm}}', { source: 'こんにちは', target: '您好', tip: '' }],
    ['GET', '/v1/term-banks/{{tbank}}/terms', null],
    ['PUT', '/v1/term-banks/{{tbank}}', {
      name: 'tb_r' + STAMP.slice(-6),
      tip: 'renamed',
      source_language_id: '67b231b35781d7125d41e2a5',
      target_language_id: '67b231b35781d7125d41e2a8',
    }],
    ['GET', '/v1/teams/' + cfg.team + '/term-banks', null],
    ['DELETE', '/v1/terms/{{tterm}}', null],
    ['DELETE', '/v1/term-banks/{{tbank}}', null],
    ['GET', '/v1/teams/' + cfg.team + '/term-banks', null],
  ],

  fileOrder: [
    ['POST', '/v1/projects/' + cfg.project + '/files', {
      $form: {
        name: 'order_a.png', type: 'image/png',
        content: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        fields: {},
      },
    }, { of1: 'id' }],
    ['POST', '/v1/projects/' + cfg.project + '/files', {
      $form: {
        name: 'order_b.png', type: 'image/png',
        content: 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==',
        fields: {},
      },
    }, { of2: 'id' }],
    ['GET', '/v1/projects/' + cfg.project + '/files/order', null],
    ['PUT', '/v1/projects/' + cfg.project + '/files/order', { file_ids: ['{{of2}}', '{{of1}}'], version: '0' }],
    ['GET', '/v1/projects/' + cfg.project + '/files/order', null],
    ['DELETE', '/v1/files/{{of1}}', null],
    ['DELETE', '/v1/files/{{of2}}', null],
  ],

  invitations: [
    ['POST', '/v1/projects/' + cfg.project + '/invitations', {
      user_id: cfg.stranger,
      role: cfg.projectRole,
      tags: ['admin'],
      message: '欢迎加入',
    }, { invitation: 'invitation.id' }],
    ['GET', '/v1/projects/' + cfg.project + '/invitations', null],
    ['DELETE', '/v1/invitations/{{invitation}}', null],
    ['GET', '/v1/projects/' + cfg.project + '/invitations', null],
  ],

  members: [    ['GET', '/v1/projects/' + cfg.project + '/members', null],
    ['POST', '/v1/projects/' + cfg.project + '/members/changes', {
      operations: [{
        operation_id: 'op-' + STAMP,
        action: 'add',
        user_id: '67b24e464751693d80973c35',
        tags: ['translator'],
      }],
    }],
    ['GET', '/v1/projects/' + cfg.project + '/members', null],
    ['GET', '/v1/projects/' + cfg.project, null],
  ],

  reads: [
    ['GET', '/v1/site/homepage', null],
    ['GET', '/v1/languages', null],
    ['GET', '/v1/user/info', null],
    ['GET', '/v1/user/teams', null],
    ['GET', '/v1/user/projects?limit=5', null],
    ['GET', '/v1/teams/' + cfg.team + '/projects', null],
    ['GET', '/v1/projects/' + cfg.project, null],
    ['GET', '/v1/projects/' + cfg.project + '/files', null],
    ['GET', '/v1/projects/' + cfg.project + '/targets', null],
    ['GET', '/v1/projects/' + cfg.project + '/members', null],
    ['GET', '/v1/admin/users?limit=3', null],
    ['GET', '/v1/storage-pool/sources', null],
  ],
};

// ---- machinery ------------------------------------------------------------

function resolveTemplate(value, side) {
  if (typeof value === 'string') {
    return value.replace(/\{\{(\w+)\}\}/g, (_, key) => {
      const id = side.ids[key];
      if (!id) throw new Error('unresolved alias ' + key + ' on ' + side.name);
      return id;
    });
  }
  if (Array.isArray(value)) return value.map((v) => resolveTemplate(v, side));
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value)) out[k] = resolveTemplate(v, side);
    return out;
  }
  return value;
}

function pick(body, dotted) {
  let cur = body;
  for (const part of dotted.split('.')) {
    if (cur == null) return undefined;
    cur = cur[part];
  }
  return cur;
}

async function call(side, method, path, body) {
  const url = side.base + resolveTemplate(path, side);
  const options = {
    method,
    headers: { Authorization: 'Bearer ' + TOKEN, Accept: 'application/json' },
    signal: AbortSignal.timeout(60000),
  };
  if (body && body.$form) {
    const spec = body.$form;
    const form = new FormData();
    form.append('file', new Blob([Buffer.from(spec.content, 'base64')], { type: spec.type }), spec.name);
    for (const [k, v] of Object.entries(spec.fields ?? {})) form.append(k, String(v));
    options.body = form;
  } else if (body != null) {
    options.headers['Content-Type'] = 'application/json';
    options.body = JSON.stringify(resolveTemplate(body, side));
  }
  const res = await fetch(url, options);
  const text = await res.text();
  let parsed = null;
  try { parsed = text.length ? JSON.parse(text) : null; } catch { parsed = text.slice(0, 200); }
  return { status: res.status, body: parsed };
}

const VOLATILE = /(^|_)(time|at|date|expires|signature|etag|revision|hash|token|cursor|password)$/i;

function normalize(value, side, key = '') {
  if (typeof value === 'string') {
    // Replace this side's recorded ids with the shared alias form.
    for (const [alias, id] of Object.entries(side.ids)) {
      if (id && value === id) return '{{' + alias + '}}';
      if (id && value !== id && value.includes(id)) value = value.split(id).join('{{' + alias + '}}');
    }
    if (/^https?:/.test(value)) return value.split('?')[0];
    return value;
  }
  if (Array.isArray(value)) {
    return { $array: value.length, $first: value.length ? normalize(value[0], side) : null };
  }
  if (value && typeof value === 'object') {
    const out = {};
    for (const [k, v] of Object.entries(value).sort(([a], [b]) => a.localeCompare(b))) {
      out[k] = VOLATILE.test(k) && typeof v !== 'object' ? '<' + typeof v + '>' : normalize(v, side, k);
    }
    return out;
  }
  return value;
}

function diff(py, cs, at, out) {
  const t = (v) => v === null ? 'null' : Array.isArray(v) ? 'array' : typeof v;
  if (t(py) !== t(cs)) { out.push({ kind: 'type', at, py: t(py), cs: t(cs) }); return; }
  if (t(py) === 'array') {
    if (py.$array !== cs.$array) out.push({ kind: 'array-length', at, py: py.$array, cs: cs.$array });
    if (py.$first && cs.$first) diff(py.$first, cs.$first, at + '[]', out);
    return;
  }
  if (t(py) === 'object') {
    for (const k of Object.keys(py)) if (!(k in cs)) out.push({ kind: 'missing', at: at + '.' + k });
    for (const k of Object.keys(cs)) if (!(k in py)) out.push({ kind: 'extra', at: at + '.' + k });
    for (const k of Object.keys(py)) if (k in cs) diff(py[k], cs[k], at + '.' + k, out);
    return;
  }
  if (py !== cs) out.push({ kind: 'value', at, py, cs });
}

// ---- run ------------------------------------------------------------------

const only = process.argv.includes('--flow') ? process.argv[process.argv.indexOf('--flow') + 1] : null;
const names = only ? [only] : Object.keys(flows);
const findings = [];

for (const name of names) {
  const steps = flows[name];
  if (!steps) { console.error('unknown flow ' + name); process.exit(2); }

  for (const [index, step] of steps.entries()) {
    const [method, path, body, capture] = step;
    const label = name + '#' + (index + 1) + ' ' + method + ' ' + path;

    const py = await call(sides.py, method, path, body);
    const cs = await call(sides.cs, method, path, body);

    if (process.env.PARITY_DEBUG) {
      console.error('DBG ' + label + ' py=' + py.status + ' cs=' + cs.status);
      if (py.status >= 400) console.error('  py: ' + JSON.stringify(py.body).slice(0, 300));
      if (cs.status >= 400) console.error('  cs: ' + JSON.stringify(cs.body).slice(0, 300));
    }

    if (capture) {
      for (const [side, response] of [[sides.py, py], [sides.cs, cs]]) {
        for (const [alias, dotted] of Object.entries(capture)) {
          const value = pick(response.body, dotted);
          if (process.env.PARITY_DEBUG) console.error('CAP ' + side.name + ' ' + dotted + ' -> ' + value);
          if (value) side.ids[alias] = String(value);
        }
      }
    }

    if (py.status !== cs.status) {
      findings.push({ label, kind: 'status', py: py.status, cs: cs.status,
        pyBody: JSON.stringify(py.body).slice(0, 160), csBody: JSON.stringify(cs.body).slice(0, 160) });
      continue;
    }
    if (py.status >= 400) {
      findings.push({ label, kind: 'both-error', status: py.status,
        pyBody: JSON.stringify(py.body).slice(0, 120), csBody: JSON.stringify(cs.body).slice(0, 120) });
      continue;
    }

    const drifts = [];
    diff(normalize(py.body, sides.py), normalize(cs.body, sides.cs), '', drifts);
    if (drifts.length) findings.push({ label, kind: 'body', drifts });
  }
}

const structural = findings.filter((f) => f.kind === 'status' || (f.drifts ?? []).some((d) => d.kind !== 'value'));

if (process.env.PARITY_IDS_OUT) {
  fs.writeFileSync(process.env.PARITY_IDS_OUT, JSON.stringify({ py: sides.py.ids, cs: sides.cs.ids }, null, 2));
}
console.log(JSON.stringify({
  stamp: STAMP,
  flows: names,
  findings: findings.length,
  structural: structural.length,
  results: findings,
}, null, 2));

process.exit(structural.length ? 1 : 0);
