// TeamBuilder reference deployment for one environment (QA or Production) on Azure:
//   Azure Container Apps (API, HTTPS ingress) + Container Apps Job (migration bundle, manual)
//   + Azure SQL Database + Static Web Apps (SPA/PWA) + Key Vault (secrets) + Log Analytics.
//
// This is a reference, not provisioned infrastructure: nothing in the repository creates these
// resources, and no subscription, tenant or resource name here refers to a real deployment.
// Deploy into a resource group of the environment's own subscription or group, e.g.
//   az deployment group create -g <qa-resource-group> -f deploy/azure/main.bicep -p deploy/azure/qa.bicepparam
// QA and Production use separate parameter files, resource groups, databases, OIDC clients,
// VAPID key pairs and Key Vaults (docs/qa/private-qa-deployment.md, "Environment separation").
//
// Secrets are never parameters of the apps: the SQL connection string and the VAPID private key
// are Key Vault secrets the operator sets (names below), read by the apps' managed identity.

targetScope = 'resourceGroup'

@description('Environment name: the API runs with ASPNETCORE_ENVIRONMENT set to this, and the database must be stamped with it.')
@allowed([
  'QA'
  'Production'
])
param environmentName string = 'QA'

@description('Short lowercase prefix for resource names, e.g. tbqa.')
@minLength(3)
@maxLength(12)
param namePrefix string

param location string = resourceGroup().location

@description('API image, pinned by tag or digest, e.g. ghcr.io/rocketdelivery2/teambuilder-api:1.0.0.')
param apiImage string

@description('Migration bundle image from the same release as apiImage.')
param migratorImage string

@description('OIDC issuer/authority (https) the API validates tokens against.')
param oidcAuthority string

@description('Audience the provider puts in API access tokens.')
param oidcApiAudience string

@description('Claim holding the stable user id: sub, or oid for Microsoft Entra ID.')
param oidcSubjectClaim string = 'sub'

@description('Exact https origins of the web client (the Static Web App URL and/or a custom domain).')
param allowedOrigins array = []

@description('Proxy networks (CIDR) allowed to set X-Forwarded-For. Leave empty unless you have confirmed the ingress source range for this environment.')
param forwardedHeadersKnownNetworks array = []

@description('Web Push on/off. The VAPID private key is the Key Vault secret "vapid-private-key".')
param webPushEnabled bool = false
param webPushSubject string = ''
param vapidPublicKey string = ''

@description('Azure SQL administrator login (break-glass only; the API uses its own database user).')
param sqlAdministratorLogin string

@secure()
@description('Azure SQL administrator password. Supply at deployment time; never commit it.')
param sqlAdministratorPassword string

param apiMinReplicas int = 1
param apiMaxReplicas int = 2

var names = {
  logs: '${namePrefix}-logs'
  environment: '${namePrefix}-cae'
  identity: '${namePrefix}-id'
  keyVault: '${namePrefix}-kv-${uniqueString(resourceGroup().id)}'
  sqlServer: '${namePrefix}-sql-${uniqueString(resourceGroup().id)}'
  database: 'TeamBuilder${environmentName}'
  api: '${namePrefix}-api'
  migrator: '${namePrefix}-migrate'
  web: '${namePrefix}-web'
}

var keyVaultSecretsUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logs
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: names.identity
  location: location
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: take(names.keyVault, 24)
  location: location
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 30
    enablePurgeProtection: true
  }
}

resource secretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, identity.id, keyVaultSecretsUserRole)
  scope: keyVault
  properties: {
    roleDefinitionId: keyVaultSecretsUserRole
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource sqlServer 'Microsoft.Sql/servers@2021-11-01' = {
  name: take(names.sqlServer, 63)
  location: location
  properties: {
    administratorLogin: sqlAdministratorLogin
    administratorLoginPassword: sqlAdministratorPassword
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

// Lets Azure services (the Container Apps environment) connect; tighten with private
// networking for Production.
resource allowAzure 'Microsoft.Sql/servers/firewallRules@2021-11-01' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

resource database 'Microsoft.Sql/servers/databases@2021-11-01' = {
  parent: sqlServer
  name: names.database
  location: location
  sku: { name: 'S0', tier: 'Standard' }
  properties: {
    requestedBackupStorageRedundancy: 'Local'
  }
}

resource backupRetention 'Microsoft.Sql/servers/databases/backupShortTermRetentionPolicies@2021-11-01' = {
  parent: database
  name: 'default'
  properties: {
    retentionDays: 7
  }
}

resource containerEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: names.environment
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

var sqlSecret = {
  name: 'sql-connection-string'
  keyVaultUrl: '${keyVault.properties.vaultUri}secrets/sql-connection-string'
  identity: identity.id
}

var vapidSecret = {
  name: 'vapid-private-key'
  keyVaultUrl: '${keyVault.properties.vaultUri}secrets/vapid-private-key'
  identity: identity.id
}

var forwardedNetworkEnv = [for (network, i) in forwardedHeadersKnownNetworks: {
  name: 'ForwardedHeaders__KnownNetworks__${i}'
  value: network
}]

var forwardedHeadersEnv = empty(forwardedHeadersKnownNetworks) ? [] : concat([ { name: 'ForwardedHeaders__Enabled', value: 'true' } ], forwardedNetworkEnv)

var webPushEnv = webPushEnabled ? [
  { name: 'WebPush__Enabled', value: 'true' }
  { name: 'WebPush__Subject', value: webPushSubject }
  { name: 'WebPush__VapidPublicKey', value: vapidPublicKey }
  { name: 'WebPush__VapidPrivateKey', secretRef: 'vapid-private-key' }
] : [
  { name: 'WebPush__Enabled', value: 'false' }
]

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: names.api
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [ secretsUser ]
  properties: {
    managedEnvironmentId: containerEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      secrets: webPushEnabled ? [ sqlSecret, vapidSecret ] : [ sqlSecret ]
    }
    template: {
      terminationGracePeriodSeconds: 40
      containers: [
        {
          name: 'api'
          image: apiImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: environmentName }
            { name: 'ConnectionStrings__TeamBuilderSql', secretRef: 'sql-connection-string' }
            { name: 'Jwt__Authority', value: oidcAuthority }
            { name: 'Jwt__Audience', value: oidcApiAudience }
            { name: 'Jwt__ExternalIdentity__SubjectClaim', value: oidcSubjectClaim }
            { name: 'AllowedOrigins', value: join(allowedOrigins, ',') }
          ], forwardedHeadersEnv, webPushEnv)
          probes: [
            {
              type: 'Startup'
              httpGet: { path: '/healthz/live', port: 8080 }
              periodSeconds: 5
              failureThreshold: 24
            }
            {
              type: 'Liveness'
              httpGet: { path: '/healthz/live', port: 8080 }
              periodSeconds: 15
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/healthz/ready', port: 8080 }
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        minReplicas: apiMinReplicas
        maxReplicas: apiMaxReplicas
      }
    }
  }
}

// Started by the operator before each API rollout: az containerapp job start -g <rg> -n <prefix>-migrate
resource migrator 'Microsoft.App/jobs@2024-03-01' = {
  name: names.migrator
  location: location
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [ secretsUser ]
  properties: {
    environmentId: containerEnvironment.id
    configuration: {
      triggerType: 'Manual'
      replicaTimeout: 900
      replicaRetryLimit: 0
      manualTriggerConfig: {
        parallelism: 1
        replicaCompletionCount: 1
      }
      secrets: [ sqlSecret ]
    }
    template: {
      containers: [
        {
          name: 'migrate'
          image: migratorImage
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ConnectionStrings__TeamBuilderSql', secretRef: 'sql-connection-string' }
          ]
        }
      ]
    }
  }
}

resource web 'Microsoft.Web/staticSites@2023-01-01' = {
  name: names.web
  location: location
  sku: { name: 'Free', tier: 'Free' }
  properties: {}
}

output apiUrl string = 'https://${api.properties.configuration.ingress.fqdn}'
output webUrl string = 'https://${web.properties.defaultHostname}'
output keyVaultName string = keyVault.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = database.name
output migratorJobName string = migrator.name
