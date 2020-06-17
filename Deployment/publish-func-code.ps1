
  # This Sample Script is provided for the purpose of illustration only and is not intended to be used 
  # in a production environment. THIS SAMPLE CODE AND ANY RELATED INFORMATION ARE PROVIDED "AS IS" 
  # WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED 
  # WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE. We grant You a nonexclusive, 
  # royalty-free right to use and modify the Sample Script and to reproduce and distribute the object code 
  # form of the Sample Script, provided that You agree: (i) to not use Our name, logo, or trademarks to 
  # market Your software product in which the Sample Script is embedded; (ii) to include a valid copyright 
  # notice on Your software product in which the Sample Script is embedded; and (iii) to indemnify, hold 
  # harmless, and defend Us and Our suppliers from and against any claims or lawsuits, including attorneys’ 
  # fees, that arise or result from the use or distribution of the Sample Script.

  # CLI create commands can be run multiple times against existing resources without side effect 
  # "--not-wait" can be used where supported for concurrency (parallel commands)

function Publish-App-Code {
  [CmdletBinding()]
  Param(
    $rg,
    $funcAppName
  )
  
  $projPath='..\ImportServiceTags\ImportAzipRanges.csproj'
  $zipName = 'importlist.zip'
  
  dotnet publish $projPath
  
  write-host "built" -ForegroundColor Green
  
  $compress = @{
    Path= "..\ImportServiceTags\bin\Debug\netcoreapp3.1\publish\*"
    CompressionLevel = "Fastest"
    DestinationPath = $zipName
  }
  Compress-Archive @compress -Force
  
  write-host "zipped" -ForegroundColor Green
  
  az functionapp deployment source config-zip  -g $rg -n $funcAppName --src $zipName 
  
  write-host "published app code" 
  

}

# depends on publish-app-code, but publish-app-code can be used independently 
function Publish-Infra {
  [CmdletBinding()]
  Param(
    $location = 'westeurope',
    $rg = 'GetAzureServiceTagFiles5-rg',
    $fileStorageAccName = "servicetagfilestor",
    $funcAppName=  "servicetagfile-func",
    $funcAppStor=  "servicetagfilefuncstor"
  )
  
  #rg
  az group create --location $location --name $rg
  
  #storage
  $fileStorAcc =az storage account create --name $fileStorageAccName --resource-group $rg | ConvertFrom-Json
  $storKeys=az storage account keys list --account-name $fileStorageAccName  | ConvertFrom-Json
  # storage cnn
  $key =$storKeys[0].value
  $storageConnectionString = "DefaultEndpointsProtocol=https;EndpointSuffix=core.windows.net;AccountName=$fileStorageAccName;AccountKey=$key"
  
  #func
  az storage account create --name $funcAppStor --resource-group $rg
  # CLI will create both linked app insights resource and ikey setting => "AppInsights_Instrumentationkey"
  # and functions storage account connection string app setting => "AzureWebJobsStorage"
  az functionapp create --name $funcAppName --resource-group $rg --storage-account $funcAppStor --functions-version 3 --os-type Windows --runtime dotnet --consumption-plan-location $location
  
  # add MSI to func app (to call service tag endpoint)
  $account = az account show | ConvertFrom-Json
  $subscriptionId = $account.id
  $tenantId = $account.homeTenantId
  $scope = "/subscriptions/$subscriptionId"
  az functionapp identity assign -g $rg -n $funcAppName --role reader --scope $scope
  
  # add func app setting
  az functionapp config appsettings set --name $funcAppName --resource-group $rg --settings "FileStorAcc=$storageConnectionString"
  az functionapp config appsettings set --name $funcAppName --resource-group $rg --settings "TenantId=$tenantId"
  az functionapp config appsettings set --name $funcAppName --resource-group $rg --settings "Region=$location"
  az functionapp config appsettings set --name $funcAppName --resource-group $rg --settings "SubscriptionId=$subscriptionId"
  

  # create storage dependencies 
  az storage container create --name 'azure-ip-ranges' --account-key $key --account-name $fileStorageAccName
  az storage queue create --name 'nextazureipfile' --account-key $key --account-name $fileStorageAccName 
  # AEG subscription to storage event
  
  # publish function code so that the eventgrid can be bound 
  write-host "sleeping for func SCM site to become available" 
  Start-Sleep -seconds 180
  write-host "publishing app code" 
  Publish-App-Code -rg $rg -funcAppName $funcAppName
  
  #--topic-name is deprecated using --source-resource-id
  # creating the subscription to the function also issues a validation handshake

  # get fn sys key code (still need to do this with REST AFAIK no CLI right now)
  $funcResourceId = "/subscriptions/$subscriptionId/resourceGroups/$rg/providers/Microsoft.Web/sites/" + $funcAppName
  $resp = az rest --method post --uri "$funcResourceId/host/default/listKeys?api-version=2018-11-01" | Out-String | ConvertFrom-Json
  $eventGridKeyCode=$resp.systemKeys.eventgrid_extension

  # ensure the & in the endpoint URL is escaped (ps1 limitation) ref https://github.com/Azure/Azure-Functions/issues/1007
  $endpoint="https://$funcAppName.azurewebsites.net/runtime/webhooks/EventGrid?functionName=ImportIpsActionDelta^^^&code=" + $eventGridKeyCode
  az eventgrid event-subscription create --source-resource-id $fileStorAcc.Id --name "servicetagchangeset" `
  --endpoint $endpoint --included-event-types Microsoft.Storage.BlobCreated --endpoint-type Webhook

  write-host "published infra" 
}

#entry point
Publish-Infra

#comment above then, uncomment below - to push just func code
#Publish-App-Code -rg 'GetAzureServiceTagFiles5-rg' -funcAppName 'servicetagfile-func'#