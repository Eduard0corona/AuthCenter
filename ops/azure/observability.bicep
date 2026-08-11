@description('Azure region for observability resources.')
param location string = resourceGroup().location

@minLength(4)
param workspaceName string

@minLength(4)
param applicationInsightsName string

resource workspace 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: workspaceName
  location: location
  properties: {
    retentionInDays: 30
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
    sku: { name: 'PerGB2018' }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: applicationInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    IngestionMode: 'LogAnalytics'
    WorkspaceResourceId: workspace.id
    RetentionInDays: 30
    DisableIpMasking: false
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
  }
}

output applicationInsightsResourceId string = applicationInsights.id
