[CmdletBinding()]
Param(

)

$rg = 'GetAzureServiceTagFiles-rg'
$funcAppName=  "servicetagfile-func"
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

write-host "published" -ForegroundColor Green

