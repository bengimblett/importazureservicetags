using System;
using Microsoft.Azure.WebJobs;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.IO;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.AspNetCore.Mvc;
using Azure.Storage.Blobs;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ImportAzIpRanges.Tests")]
namespace ImportAzIpRanges
{
    public class ServiceTagFile
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public string Type { get; set; }
        public string ChangeNumber { get; set; }
        public string Cloud { get; set; }

        public List<ServiceTagResult> Values { get; set;}
    }

    public class ServiceTagResult
    {
        public string Name { get; set; }
        public string Id { get; set; }
        public ServiceTagProperties Properties { get; set; }
    }

    public   class ServiceTagProperties{
        public string ChangeNumber { get; set; }
        public string Region { get; set; }

        public string SystemService { get; set; }

        public List<string> AddressPrefixes { get; set; }

    }

    public interface IStorageProvider {
        Task<bool> WriteStreamAsBlobToStorageAsync(Stream content, string containerName,string filename);
        Task<Stream> ReadFileFromBlobToStorageAsync(string containerName,string filename);
    }

    public class StorageProvider: IStorageProvider{

        public async Task<bool> WriteStreamAsBlobToStorageAsync(Stream content, string containerName,string filename){

            var connectionString = Environment.GetEnvironmentVariable("FileStorAcc");
            var blobClient = new BlobServiceClient(connectionString);
            
            var containerClient =  blobClient.GetBlobContainerClient(containerName);
            
            await containerClient.CreateIfNotExistsAsync();

            // Get a reference to a blob
            var blob = containerClient.GetBlobClient(filename);

            // Open the file and upload its data

            await blob.UploadAsync(content, true);
            content.Close();

            return true;
        }

        public async Task<Stream> ReadFileFromBlobToStorageAsync(string containerName,string filename){
            
            var connectionString = Environment.GetEnvironmentVariable("FileStorAcc");

            var container = new BlobContainerClient(connectionString, containerName);
            var blockBlob = container.GetBlobClient(filename);
            var memoryStream = new MemoryStream();

            if ( blockBlob.Exists()){
                await blockBlob.DownloadToAsync(memoryStream);
                memoryStream.Seek(0,SeekOrigin.Begin); //rewind
                return memoryStream;
            }

            return null;
        }

                
        public async Task<bool> FileExistsBlobStorageAsync(string containerName,string filename){
            
            var connectionString = Environment.GetEnvironmentVariable("FileStorAcc");

            var container = new BlobContainerClient(connectionString, containerName);
            var blockBlob = container.GetBlobClient(filename);
            var memoryStream = new MemoryStream();

            return await blockBlob.ExistsAsync();
  
        }

    }

    public static class AzureIpFileFunctions
    {
 
        private static HttpClient _httpClient = new HttpClient();
        private const string _containerName =   "azure-ip-ranges";

        // 3
        // receives a pointer to delta of changes (file, storage blob) 
        // then applies them 
        // run only one instance

        [FunctionName("ImportIpsActionDelta")]
        [Singleton(Mode=SingletonMode.Listener)]
        public static async Task  FnActionDelta(
            [QueueTrigger("actiondelta" ,Connection="FileStorAcc")] string fileName, 
            ILogger log)
        {
            var storage= new StorageProvider();
            var newFileName = $"{fileName}.completed";

            if ( string.IsNullOrWhiteSpace(fileName)){
                log.LogInformation($"No delta received. No further action.");
                return;
            }
            // set to run as singleton so this is belt and brace
            if ( await storage.FileExistsBlobStorageAsync(_containerName,newFileName)){
                log.LogInformation($"This delta received already and completed. No further action.");
                return;
            }

            log.LogInformation($"delta {fileName} received");

            //
            // action delta
            // 
            
            var newFile = new ServiceTagFile() { ChangeNumber= fileName };
            var stream =StringToStream(SerializeFile(newFile));
            await storage.WriteStreamAsBlobToStorageAsync(stream,_containerName,newFileName);
            // enqueue reference to delta for function to write access restrictions
            log.LogInformation($"C# function actioned {fileName} and wrote completed {newFileName}");
        }

        // 2
        // receives a pointer to latest file from the trigger function 
        // looks for any prior
        // compares for any delta/changes
        // run only once instance

        [FunctionName("ImportIpsCompare")]
        [Singleton(Mode=SingletonMode.Listener)]
        [return: Queue("actiondelta", Connection="FileStorAcc")]
        public static async Task<string>  FnCompareFiles(
            [QueueTrigger("nextazureipfile", Connection="FileStorAcc")] string fileName, 
            ILogger log)
        {
            var storage= new StorageProvider();
            // in case of multiple runs check so see if there's already a delta - set to run as singleton , so this is belt and brace
            var existingDelta = await storage.FileExistsBlobStorageAsync(_containerName, $"{fileName}.delta");
            if ( existingDelta )
            {
                log.LogInformation($"Existing Delta found for {fileName}, duplicate run for the same file?");
                return string.Empty;
            }
            // check for a previous change number
            var currentFileStream = await storage.ReadFileFromBlobToStorageAsync(_containerName,fileName);
            var currentFile = DeserializeFile( StreamToString(currentFileStream));
            if ( currentFile == null ){
                throw new Exception($"No valid file named {fileName} for container {_containerName}");
            }
            var previousFile = await GetPreviousFileAsync(storage,_containerName,fileName);

            var delta = CompareFiles(currentFile,previousFile);

            if (null != delta){
                var newFileName = $"{fileName}.delta";
                var newFile = new ServiceTagFile() { Values=delta };
                var stream =StringToStream(SerializeFile(newFile));
                await storage.WriteStreamAsBlobToStorageAsync(stream,_containerName,newFileName);
                // enqueue reference to delta for function to write access restrictions
                log.LogInformation($"C# function processed: {fileName} and wrote delta {newFileName}");
                return newFileName;
            }

            log.LogInformation($"C# function processed: {fileName}. No delta.");
            return string.Empty;
        }
        
        // 1
        // Trigger function / Task runner
        // every sunday (weekly)
        //
        // For Local test/debug invocation with FUNC CORE TOOLS then POST to http://localhost:{port}/admin/functions/{function_name}

        [FunctionName("ImportIps")]
        [return: Queue("nextazureipfile", Connection="FileStorAcc")]
        public static async Task<string> FnEntryPointRun([TimerTrigger("0 0 0 * * 0")]TimerInfo myTimer ,
            ILogger log)
        {

                var tenantId = "72f988bf-86f1-41af-91ab-2d7cd011db47";// Environment.GetEnvironmentVariable("TenantId");
                var region = "westeurope"; // Environment.GetEnvironmentVariable("Region");
                var subscriptionId = "09a55fcd-23ab-4b16-98b7-c17f85e641d5"; //  Environment.GetEnvironmentVariable("SubscriptionId");
                var azureServiceTokenProvider = new AzureServiceTokenProvider();
                var token = await azureServiceTokenProvider.GetAccessTokenAsync("https://management.azure.com", tenantId);

                var url = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.Network/locations/{region}/serviceTags?api-version=2020-04-01";
                _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                var response = await _httpClient.GetAsync(url);

                log.LogInformation("Received Service Tag List");

                if ( response.StatusCode == System.Net.HttpStatusCode.OK ){
                    var changeNumber = GetChangeNumber(await response.Content.ReadAsStringAsync());
                   
                    var responseFile = new MemoryStream();
                    await response.Content.CopyToAsync(responseFile);
                    responseFile.Seek(0,SeekOrigin.Begin);//rewind
                    await new StorageProvider().WriteStreamAsBlobToStorageAsync(responseFile,_containerName,changeNumber);
                    return changeNumber;
                }
                else{
                    log.LogInformation("Unexpected API Status " + response.StatusCode);
                    throw new Exception("unable to call REST API for Az DC IP Ranges");
                }
            
        }

        internal static string GetChangeNumber(string json){
            
            var options = new JsonSerializerOptions() {
                PropertyNameCaseInsensitive = true,
                IgnoreNullValues = true
            };
            var file= JsonSerializer.Deserialize<ServiceTagFile>(json, options);
            return file.ChangeNumber;
        }

        internal static bool CompareChangeNumbers(ServiceTagResult current, ServiceTagResult prior){
            var currentId = int.Parse(current.Properties.ChangeNumber);
            var priorId = int.Parse(prior.Properties.ChangeNumber);
            return currentId > priorId;
        }

        internal static List<ServiceTagResult> CompareFiles(ServiceTagFile currentFile, ServiceTagFile previousFile){
            

            if ( currentFile == null || currentFile.Values == null || currentFile.Values.Count == 0){
                throw new Exception("Invalid current file, no values");
            }
            if ( previousFile == null ){
                // no prior file , no delta
                return currentFile.Values;
            }
            if ( previousFile.Values == null || previousFile.Values.Count==0){
                throw new Exception("prior file found, but no values");
            }

            var delta = new List<ServiceTagResult>();

            foreach ( var value in currentFile.Values){
                var priorValueById=previousFile.Values.Find(v=> v.Id==value.Id);
                if ( null ==priorValueById ){
                    // did not exist before, or must be new this time as current value property is #1
                    delta.Add(value);
                }
                else
                {
                    // compare versions
                    var isNewer=CompareChangeNumbers(value, priorValueById);
                    if ( isNewer){
                        delta.Add(value);
                    }
                }
            }

            if ( delta.Count==0){
                delta=null;
            }
            return delta;
        }

        


        internal static async Task<ServiceTagFile> GetPreviousFileAsync(IStorageProvider storageProvider, string containerName, string fileName){
            if ( string.IsNullOrWhiteSpace(fileName)){
                return null;
            }
            int changeNumber=0;
            if ( !int.TryParse(fileName, out changeNumber)){
                return null;
            }
            int i = 1;
            while ( i < 3 ) // go back two versions
            {
                // try a couple of times, valid changenumber will be high to date
                var priorFileName = (changeNumber-i).ToString();
                var priorFileStream = await storageProvider.ReadFileFromBlobToStorageAsync(containerName,priorFileName);
                if ( priorFileStream != null && priorFileStream.Length>0  )
                {
                    var priorFile = DeserializeFile(StreamToString(priorFileStream));
                    return priorFile;
                }
                
                i++;
            }

            return null; // this does not account for there being a longer gap than 2
        }

        internal static Stream StringToStream(string input){
            var stream = new MemoryStream();

            var writer = new StreamWriter(stream);
            writer.Write(input);
            writer.Flush();
            stream.Seek(0,SeekOrigin.Begin);
     
            return stream;
        }

        internal static string StreamToString(Stream stream){
            stream.Seek(0,SeekOrigin.Begin);
            using (StreamReader reader = new StreamReader(stream, System.Text.Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        internal static string SerializeFile(ServiceTagFile file){
            var options = new JsonSerializerOptions() {
                PropertyNameCaseInsensitive = true,
                IgnoreNullValues = true
            };
            return JsonSerializer.Serialize(file, typeof(ServiceTagFile), options);
        }

        internal static ServiceTagFile DeserializeFile(string json){
            
            var options = new JsonSerializerOptions() {
                PropertyNameCaseInsensitive = true,
                IgnoreNullValues = true
            };
            return JsonSerializer.Deserialize<ServiceTagFile>(json, options);
        }

    }
}
