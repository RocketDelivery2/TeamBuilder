import type { ExpoConfig } from 'expo/config';
const environment = process.env.EXPO_PUBLIC_ENVIRONMENT ?? 'local';
if (!['local', 'qa', 'production'].includes(environment)) throw new Error('Invalid app environment.');
const production = environment === 'production';
const projectId = process.env.EXPO_PUBLIC_EAS_PROJECT_ID;
const config: ExpoConfig = {
  name: production ? 'TeamBuilder' : 'TeamBuilder QA',
  slug: 'teambuilder-mobile',
  version: '0.1.0',
  scheme: production ? 'teambuilder' : 'teambuilder-qa',
  orientation: 'default',
  userInterfaceStyle: 'automatic',
  icon: './assets/icon.png',
  ios: {
    bundleIdentifier: production ? 'info.teambuilder.mobile' : 'info.teambuilder.mobile.qa',
    supportsTablet: true,
    infoPlist: { ITSAppUsesNonExemptEncryption: false },
  },
  android: {
    package: production ? 'info.teambuilder.mobile' : 'info.teambuilder.mobile.qa',
    adaptiveIcon: { foregroundImage: './assets/icon.png', backgroundColor: '#0b1220' },
    blockedPermissions: ['android.permission.ACCESS_BACKGROUND_LOCATION', 'android.permission.RECORD_AUDIO'],
  },
  plugins: [
    'expo-router',
    ['expo-secure-store', { configureAndroidBackup: true }],
    ['expo-location', { locationWhenInUsePermission: 'Find games near the location you choose to share for this search.', isAndroidBackgroundLocationEnabled: false, isAndroidForegroundServiceEnabled: false }],
    ['expo-notifications', { defaultChannel: 'roster-vacancies' }],
    ['expo-splash-screen', { image: './assets/icon.png', backgroundColor: '#0b1220', imageWidth: 160 }],
    'expo-web-browser',
  ],
  extra: projectId ? { eas: { projectId } } : {},
};
export default config;
