// QA parameters for main.bicep. Every value below is a placeholder to replace; none refers to
// a provisioned resource. Secrets are read from the deploying shell's environment, never stored here.
using './main.bicep'

param environmentName = 'QA'
param namePrefix = 'tbqa'
param apiImage = 'ghcr.io/rocketdelivery2/teambuilder-api:0.0.0-replace'
param migratorImage = 'ghcr.io/rocketdelivery2/teambuilder-migrator:0.0.0-replace'
param oidcAuthority = 'https://replace-with-qa-issuer.example/'
param oidcApiAudience = 'api://replace-with-qa-api-audience'
param oidcSubjectClaim = 'sub'
param allowedOrigins = [
  'https://replace-with-qa-web-origin.example'
]
param webPushEnabled = false
param webPushSubject = 'mailto:replace-with-qa-contact@example.com'
param vapidPublicKey = ''
param sqlAdministratorLogin = 'tbqaadmin'
param sqlAdministratorPassword = readEnvironmentVariable('TEAMBUILDER_QA_SQL_ADMIN_PASSWORD', '')
