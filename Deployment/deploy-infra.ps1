[CmdletBinding()]
Param(

)

# CLI create commands can be run multiple times against existing resources without side effect 
# "--not-wait" can be used where supported for concurrency (parallel commands)

#vars
$location = 'westeurope'
$rg = 'GetAzureServiceTagFiles-rg'
$fileStorageAccName = "azservicetagfilestor"
$funcAppName=  "servicetagfile-func"
$funcAppStor=  "servicetagfilefnstor"

#rg
az group create --location $location --name $rg

#storage
az storage account create --name $fileStorageAccName --resource-group $rg
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

# create dependencies 
az storage container create --name 'azure-ip-ranges' --account-key $key --account-name $fileStorageAccName
az storage queue create --name 'actiondelta' --account-key $key --account-name $fileStorageAccName
az storage queue create --name 'nextazureipfile' --account-key $key --account-name $fileStorageAccName 



write-host "infra created - use function deploy script to zip-deploy function code"
write-host "note: cannot push zip file as soon as the function is created - because the SCM site takes time to come up"