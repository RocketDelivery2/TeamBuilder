import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';

const businessConflicts = new Counter('business_conflicts');
const transportErrors = new Counter('transport_server_errors');
const timeouts = new Counter('timeouts');
const baseUrl = __ENV.BASE_URL;
const profile = (__ENV.SCENARIO || 'smoke').toLowerCase();
const duration = __ENV.DURATION || (profile === 'smoke' ? '10s' : '30s');
const vus = Number(__ENV.VUS || (profile === 'smoke' ? 1 : profile === 'baseline' ? 5 : 1));
const arrivalRate = Number(__ENV.ARRIVAL_RATE || (profile === 'baseline' ? 5 : 1));
const heavyProfiles = ['stress', 'spike', 'soak'];

if (!baseUrl) {
  throw new Error('BASE_URL is required (absolute http:// or https:// URL).');
}
if (!/^https?:\/\/[^/\s]+(?:\/.*)?$/i.test(baseUrl)) {
  throw new Error('BASE_URL must be an absolute HTTP(S) URL.');
}
if (!Number.isInteger(vus) || vus < 1 || !Number.isInteger(arrivalRate) || arrivalRate < 1) {
  throw new Error('VUS and ARRIVAL_RATE must be positive integers.');
}
if (heavyProfiles.includes(profile) && __ENV.ALLOW_HEAVY !== 'true') {
  throw new Error(`${profile} requires explicit ALLOW_HEAVY=true.`);
}
const durationMatch = /^(\d+(?:\.\d+)?)(s|m|h)$/.exec(duration);
if (!durationMatch) throw new Error('DURATION must use seconds, minutes, or hours (for example 10s or 2m).');
const durationSeconds = Number(durationMatch[1]) * ({ s: 1, m: 60, h: 3600 }[durationMatch[2]]);
const estimatedRequests = profile === 'smoke' ? vus * durationSeconds * 40 : arrivalRate * durationSeconds;
if ((estimatedRequests > 100000 || vus > 100) && __ENV.ALLOW_HEAVY !== 'true') {
  throw new Error('More than 100,000 requests or over 100 VUs requires explicit ALLOW_HEAVY=true.');
}
if (!['smoke', 'baseline', 'stress', 'spike', 'soak'].includes(profile)) {
  throw new Error(`Unknown SCENARIO '${profile}'.`);
}

export const options = {
  scenarios: {
    api: profile === 'smoke'
      ? { executor: 'constant-vus', vus, duration }
      : { executor: 'constant-arrival-rate', rate: arrivalRate, timeUnit: '1s', duration, preAllocatedVUs: vus, maxVUs: heavyProfiles.includes(profile) ? Math.max(vus, Number(__ENV.MAX_VUS || 10)) : vus },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
  },
};

function request(path, label) {
  const headers = __ENV.AUTH_TOKEN ? { Authorization: `Bearer ${__ENV.AUTH_TOKEN}` } : {};
  const response = http.get(`${baseUrl.replace(/\/$/, '')}${path}`, { headers, tags: { endpoint: label } });
  if (response.status === 409) businessConflicts.add(1);
  if (response.status >= 500 || response.status === 0) transportErrors.add(1);
  if (response.error_code === 1050) timeouts.add(1);
  check(response, { [`${label} returned a non-server response`]: (r) => r.status > 0 && r.status < 500 });
}

export default function () {
  request('/health', 'health');
  request('/health/ready', 'readiness');
  request('/api/v1/teams?page=1&pageSize=20', 'public-team-list');
  request('/api/v1/events?page=1&pageSize=20', 'public-event-list');
  sleep(0.1);
}
