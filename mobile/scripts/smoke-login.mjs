// Developer check, not an app code path: signs in through the generated client against a running API.
// It calls the API port directly only because the reverse proxy does not exist until M5.
// Once the proxy exists, point API_BASE_URL at the proxy instead.
//
//   API_BASE_URL=http://localhost:8080 ACCOUNT=requester@servicedesk.local DEMO_PASSWORD=... npm run smoke:login
//
// Needs Node 22.18+ or 23.6+ (type stripping on by default). Never prints the password or the token.
import { register } from 'node:module';

// The generated files import './client.gen' without an extension; resolve those to .ts or /index.ts.
const hook = `
import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
export async function resolve(specifier, context, next) {
  if (specifier.startsWith('.') && context.parentURL?.includes('/src/api/generated/')) {
    for (const suffix of ['.ts', '/index.ts']) {
      const url = new URL(specifier + suffix, context.parentURL);
      if (existsSync(fileURLToPath(url))) return next(url.href, context);
    }
  }
  return next(specifier, context);
}`;
register('data:text/javascript,' + encodeURIComponent(hook));

const baseUrl = process.env.API_BASE_URL?.trim().replace(/\/+$/, '');
const account = process.env.ACCOUNT?.trim();
const password = process.env.DEMO_PASSWORD;
if (!baseUrl || !account || !password) {
  console.error('Set API_BASE_URL, ACCOUNT and DEMO_PASSWORD.');
  process.exit(2);
}

const { login } = await import('../src/api/generated/servicedesk/index.ts');
const { client } = await import('../src/api/generated/servicedesk/client.gen.ts');
client.setConfig({ baseUrl });

const result = await login({ body: { identifier: account, password } });
if (result.data) {
  console.log(`signed in as ${account}, token expires ${result.data.expiresAt}`);
} else {
  console.error(`sign-in failed for ${account}: ${result.response ? `HTTP ${result.response.status}` : 'server not reachable'}`);
  process.exit(1);
}
