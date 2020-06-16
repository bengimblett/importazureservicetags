[CmdletBinding()]
Param(

)

# CLI create commands can be run multiple times against existing resources without side effect 
# "--not-wait" can be used where supported for concurrency (parallel commands)

#vars
$location = 'westeurope'
$rg = 'GetAzureServiceTagFiles-rg'
$fileStorageAccName = "azservicetagfilestor"
$containerName="azure-ip-ranges"
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
#func app setting
az functionapp config appsettings set --name $funcAppName --resource-group $rg --settings "FileStorAcc=$storageConnectionString"
#
az storage container create --name $containerName --account-key $key --account-name $fileStorageAccName
az storage queue create --name 'actiondelta' --account-key $key --account-name $fileStorageAccName
az storage queue create --name 'nextazureipfile' --account-key $key --account-name $fileStorageAccName 

write-host "infra created - use function deploy script to zip-deploy function code"