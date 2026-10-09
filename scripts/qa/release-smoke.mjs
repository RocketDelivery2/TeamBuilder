#!/usr/bin/env node
// TeamBuilder private-QA release smoke: the basketball refill loop against a running API,
// automated wherever no human or real device is needed. Node 20+, no dependencies.
//
//   TB_API_URL=https://qa.example.test TB_WEB_URL=https://qa.example.test \
//   TB_SMOKE_TOKENS="<host>,<player A>,<player B>,<player C>" node scripts/qa/release-smoke.mjs
//
// Sign-in, one of:
//   TB_SMOKE_TOKENS               four access tokens from the real OIDC provider (four QA test
//                                 accounts), comma separated. Never logged.
//   TEAMBUILDER_JWT_SIGNING_KEY   LocalQA/Development API only: mints four developer tokens
//                                 (TEAMBUILDER_JWT_ISSUER / TEAMBUILDER_JWT_AUDIENCE as for
//                                 `npm run qa:token`). A QA or Production API rejects these.
//   neither                       anonymous checks only (health, readiness, web, headers).
//
// Other settings: TB_HEALTH_URL (where /healthz is reachable, default TB_API_URL; the release
// proxy does not route it publicly, so set it to skip there), TB_SMOKE_LAT / TB_SMOKE_LON (venue point, default an
// unused spot in the Pacific so testers never see the game), TB_SMOKE_TIMEOUT_SECONDS (outbox wait, default 60),
// TB_INSECURE_TLS=1 (accept the local Caddy CA during the local release smoke only).
//
// Exit code 0 when every automated step passed. Steps that need a person or a real device are
// printed as DEVICE/HUMAN and never counted as passed. The game it creates is cancelled at the end.
import { createHmac, randomInt } from 'node:crypto';

if (process.env.TB_INSECURE_TLS === '1') process.env.NODE_TLS_REJECT_UNAUTHORIZED = '0';

const api = (process.env.TB_API_URL ?? 'http://localhost:5080').replace(/\/$/, '');
const health = (process.env.TB_HEALTH_URL ?? api).replace(/\/$/, '');
const web = process.env.TB_WEB_URL?.replace(/\/$/, '');
const lat = Number(process.env.TB_SMOKE_LAT ?? 0.0123);
const lon = Number(process.env.TB_SMOKE_LON ?? -150.0456);
const timeoutMs = Number(process.env.TB_SMOKE_TIMEOUT_SECONDS ?? 60) * 1000;

const results = [];
let failed = 0;
const record = (step, status, detail = '') => {
  results.push({ step, status, detail });
  if (status === 'FAIL') failed++;
  console.log(`${status.padEnd(6)} ${step}${detail ? `  (${detail})` : ''}`);
};
const check = (step, ok, detail = '') => record(step, ok ? 'PASS' : 'FAIL', detail);
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function call(token, method, path, body) {
  const response = await fetch(api + path, {
    method,
    headers: {
      accept: 'application/json',
      ...(token ? { authorization: `Bearer ${token}` } : {}),
      ...(body !== undefined ? { 'content-type': 'application/json' } : {}),
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  let json;
  try { json = text ? JSON.parse(text) : undefined; } catch { json = undefined; }
  return { status: response.status, json, headers: response.headers };
}

function mintToken(subject) {
  const key = process.env.TEAMBUILDER_JWT_SIGNING_KEY;
  const now = Math.floor(Date.now() / 1000);
  const payload = {
    iss: process.env.TEAMBUILDER_JWT_ISSUER ?? 'teambuilder-local-qa',
    aud: process.env.TEAMBUILDER_JWT_AUDIENCE ?? 'teambuilder-api',
    sub: subject, iat: now, nbf: now, exp: now + 3600,
  };
  const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
  const unsigned = `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}`;
  return `${unsigned}.${createHmac('sha256', key).update(unsigned).digest('base64url')}`;
}

function identities() {
  if (process.env.TB_SMOKE_TOKENS) {
    const tokens = process.env.TB_SMOKE_TOKENS.split(',').map((t) => t.trim()).filter(Boolean);
    if (tokens.length < 4) throw new Error('TB_SMOKE_TOKENS needs four tokens: host, player A, player B, player C.');
    return { mode: 'oidc', tokens: tokens.slice(0, 4) };
  }
  if (process.env.TEAMBUILDER_JWT_SIGNING_KEY) {
    const run = `${Date.now().toString(36)}${randomInt(1000, 9999)}`;
    return { mode: 'developer', tokens: ['host', 'pa', 'pb', 'pc'].map((who) => mintToken(`smoke-${run}-${who}`)) };
  }
  return { mode: 'anonymous', tokens: [] };
}

async function onboard(token, label) {
  const me = await call(token, 'GET', '/api/v1/players/me');
  if (me.status === 200) return me.json;
  if (me.status !== 404) throw new Error(`GET /players/me answered ${me.status} for ${label}`);
  const created = await call(token, 'POST', '/api/v1/players/me', {
    username: `smoke${label}${randomInt(100000, 999999)}`,
    displayName: `Smoke ${label}`,
  });
  if (created.status !== 200 && created.status !== 201) throw new Error(`onboarding ${label} answered ${created.status}`);
  return created.json;
}

async function main() {
  console.log(`TeamBuilder release smoke against ${api}`);

  // 1-2. Liveness and readiness. TB_HEALTH_URL=skip when /healthz is reachable only inside the
  // network (the release proxy does not route it); the caller then probes it there.
  if (health === 'skip') {
    record('1-2. API live and ready', 'SKIP', 'probed by the caller inside the network');
  } else {
    const live = await fetch(`${health}/healthz/live`).catch(() => null);
    const liveBody = live ? await live.json().catch(() => ({})) : {};
    check('1. API live (/healthz/live)', live?.status === 200, live ? `version ${liveBody.version} commit ${String(liveBody.commit).slice(0, 12)}` : 'unreachable');
    const ready = await fetch(`${health}/healthz/ready`).catch(() => null);
    const readyBody = ready ? await ready.json().catch(() => ({})) : {};
    check('2. API ready (/healthz/ready)', ready?.status === 200, ready ? JSON.stringify(readyBody.checks ?? {}) : 'unreachable');
  }

  // 3. Web client and its security headers.
  if (web) {
    const page = await fetch(`${web}/`).catch(() => null);
    const html = page ? await page.text() : '';
    const csp = page?.headers.get('content-security-policy') ?? '';
    check('3. web app loads', page?.status === 200 && html.includes('id="root"'));
    check('3. web CSP present (script-src self, no unsafe-inline scripts)', csp.includes("script-src 'self'") && !/script-src[^;]*unsafe-inline/.test(csp));
    const runtime = await fetch(`${web}/config.js`).catch(() => null);
    const runtimeText = runtime ? await runtime.text() : '';
    check('3. /config.js served', runtime?.status === 200 && runtimeText.includes('__TEAMBUILDER_CONFIG__'));
    const sw = await fetch(`${web}/sw.js`).catch(() => null);
    check('3. service worker served', sw?.status === 200);
  } else {
    record('3. web app loads', 'SKIP', 'TB_WEB_URL not set');
  }

  const { mode, tokens } = identities();
  if (mode === 'anonymous') {
    record('4-16. signed-in refill loop', 'SKIP', 'no TB_SMOKE_TOKENS or TEAMBUILDER_JWT_SIGNING_KEY');
    return;
  }
  record('4. OIDC login', mode === 'oidc' ? 'PASS' : 'HUMAN',
    mode === 'oidc' ? 'four provider-issued tokens accepted below' : 'developer tokens (LocalQA); real OIDC sign-in is a human step in the checklist');

  const [hostToken, aToken, bToken, cToken] = tokens;
  const unauthenticated = await call(undefined, 'GET', '/api/v1/players/me');
  check('5. /players/me rejects anonymous callers', unauthenticated.status === 401);
  const host = await onboard(hostToken, 'host');
  const players = [await onboard(aToken, 'a'), await onboard(bToken, 'b'), await onboard(cToken, 'c')];
  check('5. /players/me resolves for four accounts', !!host?.id && players.every((p) => !!p?.id) && new Set([host.id, ...players.map((p) => p.id)]).size === 4);

  // 6-7. A standalone basketball game at a private venue (address masked until joined).
  const venue = await call(hostToken, 'POST', '/api/v1/venues', {
    name: 'Smoke Court', addressLine1: '1 Smoke Test Way', city: 'Testville', stateOrProvince: 'WA',
    latitude: lat, longitude: lon, timeZoneId: 'America/Los_Angeles', venueType: 2, privacyLevel: 2,
  });
  check('6. venue created', venue.status === 201, `status ${venue.status}`);
  const start = new Date(Date.now() + 26 * 3600 * 1000);
  const created = await call(hostToken, 'POST', '/api/v1/events', {
    name: '[smoke] Release check', category: 'basketball',
    eventDateUtc: start.toISOString(), scheduledEndUtc: new Date(start.getTime() + 2 * 3600 * 1000).toISOString(),
    rosterRequirements: [{ roleCode: 'participant', requiredCount: 3 }], hostParticipates: true, venueId: venue.json?.id,
  });
  check('6. standalone basketball game created (3 spots, host plays)', created.status === 201, `status ${created.status}`);
  const occurrenceId = created.json?.id;
  if (!occurrenceId) throw new Error('No game was created; stopping.');

  try {
    const strangerView = (await call(cToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    check('7. private venue address masked before joining', strangerView?.venue?.isAddressMasked === true && !strangerView?.venue?.addressLine1);

    // 8. Discovery by distance.
    const query = new URLSearchParams({ lat: String(lat + 0.01), lon: String(lon), radiusMiles: '5', activity: 'basketball' });
    const found = await call(aToken, 'GET', `/api/v1/discover/occurrences?${query}`);
    check('8. discover finds the occurrence nearby', found.status === 200 && (found.json?.items ?? []).some((o) => o.occurrenceId === occurrenceId));

    // 9. Claims fill the roster.
    const detail = (await call(hostToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    const requirementId = detail?.requirements?.[0]?.id;
    const claimA = await call(aToken, 'POST', `/api/v1/events/${occurrenceId}/roster/claims`, { requirementId });
    const claimB = await call(bToken, 'POST', `/api/v1/events/${occurrenceId}/roster/claims`, { requirementId });
    check('9. claim (two players)', claimA.status === 201 && claimB.status === 201, `${claimA.status}/${claimB.status}`);
    const full = (await call(hostToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    check('9. roster READY 3/3', full?.isRosterReady === true && full?.supplyCount === 3);
    const memberView = (await call(aToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    check('7. address visible after joining', memberView?.venue?.isAddressMasked === false && !!memberView?.venue?.addressLine1);

    // 11. Notify me (player C on the full game).
    const notify = await call(cToken, 'PUT', `/api/v1/events/${occurrenceId}/roster/requirements/${requirementId}/subscription`);
    check('11. Notify me when a spot opens', notify.status === 200 || notify.status === 201, `status ${notify.status}`);
    record('12. push registration exists', 'DEVICE', 'needs a real browser push subscription; see the release checklist');
    const unreadBefore = (await call(cToken, 'GET', '/api/v1/players/me/notifications/unread-count')).json?.unreadCount ?? 0;

    // 10. Leave opens a vacancy.
    const leave = await call(aToken, 'POST', `/api/v1/events/${occurrenceId}/roster/assignments/${claimA.json?.id}/leave`);
    check('10. leave', leave.status === 200, `status ${leave.status}`);
    const afterLeave = (await call(aToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    check('7. address masked again after leaving', afterLeave?.venue?.isAddressMasked === true);

    // 13-14. The outbox worker turns the vacancy into C's notification.
    const deadline = Date.now() + timeoutMs;
    let notification;
    while (Date.now() < deadline && !notification) {
      const page = (await call(cToken, 'GET', '/api/v1/players/me/notifications?pageSize=20')).json;
      notification = (page?.items ?? []).find((n) => n.occurrenceId === occurrenceId);
      if (!notification) await sleep(1000);
    }
    const unreadAfter = (await call(cToken, 'GET', '/api/v1/players/me/notifications/unread-count')).json?.unreadCount ?? 0;
    check('13. vacancy outbox processed', !!notification, notification ? `${Math.round((timeoutMs - (deadline - Date.now())) / 1000)} s` : `none within ${timeoutMs / 1000} s`);
    check('14. in-app notification arrived', !!notification && unreadAfter > unreadBefore);
    record('14. Web Push alert arrived on a device', 'DEVICE', 'Android Chrome and iPhone Home Screen PWA; see the release checklist');

    // 15-16. Replacement claim returns the roster to READY.
    const claimC = await call(cToken, 'POST', `/api/v1/events/${occurrenceId}/roster/claims`, { requirementId });
    check('15. replacement claim', claimC.status === 201, `status ${claimC.status}`);
    const refilled = (await call(hostToken, 'GET', `/api/v1/events/${occurrenceId}/detail`)).json;
    check('16. game returns READY 3/3', refilled?.isRosterReady === true && refilled?.supplyCount === 3);
  } finally {
    const cancelled = await call(hostToken, 'PUT', `/api/v1/events/${occurrenceId}`, { status: 5 });
    record('cleanup: smoke game cancelled', cancelled.status === 200 ? 'PASS' : 'WARN', `status ${cancelled.status}`);
  }
}

try {
  await main();
} catch (error) {
  record('smoke run', 'FAIL', error instanceof Error ? error.message : String(error));
}

const counts = results.reduce((acc, r) => ({ ...acc, [r.status]: (acc[r.status] ?? 0) + 1 }), {});
console.log(`\n${JSON.stringify(counts)}`);
if (process.env.TB_SMOKE_RESULTS) {
  const { writeFileSync } = await import('node:fs');
  writeFileSync(process.env.TB_SMOKE_RESULTS, JSON.stringify({ api, at: new Date().toISOString(), results }, null, 2));
}
process.exit(failed === 0 ? 0 : 1);
