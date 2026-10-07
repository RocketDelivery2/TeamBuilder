#!/usr/bin/env node
// Local / private-QA only: mints a short-lived HS256 bearer token for a TeamBuilder API that
// runs with a symmetric Jwt:SigningKey (the docker-compose QA stack, or `dotnet run` with a
// signing key in user-secrets). The key is read from the environment and never written
// anywhere; the token is printed to stdout for pasting into the web client's developer sign-in.
//
//   TEAMBUILDER_JWT_SIGNING_KEY=... npm run qa:token -- --sub alice
//
// Options: --sub <subject> (required), --name <display name>, --issuer, --audience, --hours.
import { createHmac } from 'node:crypto';

const args = Object.fromEntries(
  process.argv.slice(2).reduce((pairs, arg, i, all) => {
    if (arg.startsWith('--')) pairs.push([arg.slice(2), all[i + 1]]);
    return pairs;
  }, []),
);

const key = process.env.TEAMBUILDER_JWT_SIGNING_KEY;
if (!key || Buffer.byteLength(key) < 32) {
  console.error('Set TEAMBUILDER_JWT_SIGNING_KEY to the API signing key (at least 32 bytes).');
  process.exit(1);
}
if (!args.sub) {
  console.error('Usage: npm run qa:token -- --sub <subject> [--name <display name>] [--hours 8]');
  process.exit(1);
}

const now = Math.floor(Date.now() / 1000);
const hours = Number(args.hours ?? 8);
const payload = {
  iss: args.issuer ?? process.env.TEAMBUILDER_JWT_ISSUER ?? 'teambuilder-local-qa',
  aud: args.audience ?? process.env.TEAMBUILDER_JWT_AUDIENCE ?? 'teambuilder-api',
  sub: args.sub,
  ...(args.name ? { name: args.name } : {}),
  iat: now,
  nbf: now,
  exp: now + Math.round(hours * 3600),
};

const encode = (value) => Buffer.from(JSON.stringify(value)).toString('base64url');
const unsigned = `${encode({ alg: 'HS256', typ: 'JWT' })}.${encode(payload)}`;
const signature = createHmac('sha256', key).update(unsigned).digest('base64url');
process.stdout.write(`${unsigned}.${signature}\n`);
