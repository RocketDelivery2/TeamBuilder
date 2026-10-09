#!/usr/bin/env node
// Short open-loop discovery load: seeds games around a point, then sends discovery searches at
// a fixed rate and reports latency percentiles and status codes. For a local or QA API whose
// discovery rate limit has been raised for the test (RateLimiting__Discovery__PermitLimit);
// never point it at Production. Node 20+, no dependencies.
//
//   TB_API_URL=http://localhost:5080 TEAMBUILDER_JWT_SIGNING_KEY=... \
//   TB_LOAD_RPS=200 TB_LOAD_SECONDS=30 TB_LOAD_GAMES=50 node scripts/qa/discovery-load.mjs
//
// TB_SMOKE_TOKENS=<host token> may replace the signing key (games are created as that host).
// Results are a measurement of the machine it ran on, not a capacity claim.
import { createHmac, randomInt } from 'node:crypto';

const api = (process.env.TB_API_URL ?? 'http://localhost:5080').replace(/\/$/, '');
const rps = Number(process.env.TB_LOAD_RPS ?? 200);
const seconds = Number(process.env.TB_LOAD_SECONDS ?? 30);
const games = Number(process.env.TB_LOAD_GAMES ?? 50);
const lat = Number(process.env.TB_SMOKE_LAT ?? 0.0123);
const lon = Number(process.env.TB_SMOKE_LON ?? -150.0456);

function token() {
  if (process.env.TB_SMOKE_TOKENS) return process.env.TB_SMOKE_TOKENS.split(',')[0].trim();
  const now = Math.floor(Date.now() / 1000);
  const enc = (v) => Buffer.from(JSON.stringify(v)).toString('base64url');
  const unsigned = `${enc({ alg: 'HS256', typ: 'JWT' })}.${enc({
    iss: process.env.TEAMBUILDER_JWT_ISSUER ?? 'teambuilder-local-qa', aud: process.env.TEAMBUILDER_JWT_AUDIENCE ?? 'teambuilder-api',
    sub: `load-host-${now}-${randomInt(1000, 9999)}`, iat: now, nbf: now, exp: now + 3600,
  })}`;
  return `${unsigned}.${createHmac('sha256', process.env.TEAMBUILDER_JWT_SIGNING_KEY).update(unsigned).digest('base64url')}`;
}

async function call(tok, method, path, body) {
  const r = await fetch(api + path, {
    method,
    headers: { authorization: `Bearer ${tok}`, 'content-type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  return { status: r.status, json: await r.json().catch(() => undefined) };
}

const host = token();
if ((await call(host, 'GET', '/api/v1/players/me')).status === 404)
  await call(host, 'POST', '/api/v1/players/me', { username: `loadhost${randomInt(100000, 999999)}` });

const created = [];
for (let i = 0; i < games; i++) {
  const venue = await call(host, 'POST', '/api/v1/venues', {
    name: `Load Court ${i}`, latitude: lat + (i % 10) * 0.005, longitude: lon + Math.floor(i / 10) * 0.005,
    timeZoneId: 'America/Los_Angeles', venueType: 2, privacyLevel: 1,
  });
  const start = new Date(Date.now() + (24 + i) * 3600 * 1000);
  const game = await call(host, 'POST', '/api/v1/events', {
    name: `[load] Game ${i}`, category: 'basketball', eventDateUtc: start.toISOString(),
    scheduledEndUtc: new Date(start.getTime() + 7200000).toISOString(),
    rosterRequirements: [{ roleCode: 'participant', requiredCount: 10 }], hostParticipates: true, venueId: venue.json?.id,
  });
  if (game.status === 201) created.push(game.json.id);
}
console.log(`seeded ${created.length} games`);

const query = `/api/v1/discover/occurrences?${new URLSearchParams({ lat: String(lat), lon: String(lon), radiusMiles: '15', activity: 'basketball' })}`;
const sample = await call(host, 'GET', query);
const sampleCount = Array.isArray(sample.json) ? sample.json.length : (sample.json?.items?.length ?? sample.json?.results?.length ?? 'unknown');
console.log(`sample search: HTTP ${sample.status}, ${sampleCount} results`);
const latencies = [];
const statuses = {};
const inFlight = new Set();
const total = rps * seconds;
const started = performance.now();
for (let i = 0; i < total; i++) {
  const due = started + (i * 1000) / rps;
  const wait = due - performance.now();
  if (wait > 0) await new Promise((r) => setTimeout(r, wait));
  const t0 = performance.now();
  const p = fetch(api + query, { headers: { authorization: `Bearer ${host}` } })
    .then(async (r) => { await r.arrayBuffer(); statuses[r.status] = (statuses[r.status] ?? 0) + 1; })
    .catch(() => { statuses.error = (statuses.error ?? 0) + 1; })
    .finally(() => { latencies.push(performance.now() - t0); inFlight.delete(p); });
  inFlight.add(p);
}
await Promise.all(inFlight);
const elapsed = (performance.now() - started) / 1000;

latencies.sort((a, b) => a - b);
const pct = (q) => latencies[Math.min(latencies.length - 1, Math.floor(q * latencies.length))].toFixed(1);
const result = {
  target_rps: rps, seconds, requests: latencies.length, achieved_rps: +(latencies.length / elapsed).toFixed(1),
  statuses, p50_ms: +pct(0.5), p95_ms: +pct(0.95), p99_ms: +pct(0.99), max_ms: +latencies[latencies.length - 1].toFixed(1), games: created.length, results_per_search: sampleCount,
};
console.log(JSON.stringify(result, null, 2));

for (const id of created) await call(host, 'PUT', `/api/v1/events/${id}`, { status: 5 });
process.exit(Object.keys(statuses).every((s) => s === '200') ? 0 : 1);
