import { parseConfig } from '../src/config';
const env = { EXPO_PUBLIC_ENVIRONMENT: 'qa', EXPO_PUBLIC_API_BASE_URL: 'https://qa.example.invalid/api/',
  EXPO_PUBLIC_OIDC_AUTHORITY: 'https://issuer.example.invalid', EXPO_PUBLIC_OIDC_CLIENT_ID: 'public-native-client',
  EXPO_PUBLIC_OIDC_SCOPE: 'openid profile api.read' };
test('QA uses the isolated scheme and normalizes trailing slashes', () => {
  expect(parseConfig(env)).toMatchObject({ apiBaseUrl: 'https://qa.example.invalid/api', redirectUri: 'teambuilder-qa://auth/callback' });
});
test.each(['http://qa.example.invalid', 'https://user:password@qa.example.invalid', 'https://qa.example.invalid?token=x'])('rejects unsafe URL %s', value => {
  expect(() => parseConfig({ ...env, EXPO_PUBLIC_API_BASE_URL: value })).toThrow();
});
test('production uses a separate scheme', () => {
  expect(parseConfig({ ...env, EXPO_PUBLIC_ENVIRONMENT: 'production' }).redirectUri).toBe('teambuilder://auth/callback');
});
test('requires public client and OIDC scope configuration', () => {
  expect(() => parseConfig({ ...env, EXPO_PUBLIC_OIDC_CLIENT_ID: '' })).toThrow();
  expect(() => parseConfig({ ...env, EXPO_PUBLIC_OIDC_SCOPE: 'profile' })).toThrow();
});
