// # This Sample Code is provided for the purpose of illustration only and is not intended to be used 
// # in a production environment. THIS SAMPLE CODE AND ANY RELATED INFORMATION ARE PROVIDED "AS IS" 
// # WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE IMPLIED 
// # WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR PURPOSE. We grant You a nonexclusive, 
// # royalty-free right to use and modify the Sample Code and to reproduce and distribute the object code 
// # form of the Sample Code, provided that You agree: (i) to not use Our name, logo, or trademarks to 
// # market Your software product in which the Sample Code is embedded; (ii) to include a valid copyright 
// # notice on Your software product in which the Sample Code is embedded; and (iii) to indemnify, hold 
// # harmless, and defend Us and Our suppliers from and against any claims or lawsuits, including attorneys’ 
// # fees, that arise or result from the use or distribution of the Sample Code.

using System;
using Xunit;
using ImportAzIpRanges;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace ImportAzIpRanges.Tests
{


    public class MockStorageProviderReadBlobDoesNotExist : IStorageProvider{
        public async Task<bool> WriteStreamAsBlobToStorageAsync(Stream content, string containerName,string filename){

            return await Task.FromResult(true);
        }

        public async Task<Stream> ReadFileFromBlobToStorageAsync(string containerName,string filename){
            
            MemoryStream stm = null;
            return await Task.FromResult(stm);
        }

    }

    
    public class MockStorageProviderReadBlobDoesExist : IStorageProvider{

        private Stream _input;
        private string _changeNumber;

        public MockStorageProviderReadBlobDoesExist(Stream input, string changeNumber)
        {
            _input = input;
            _changeNumber = changeNumber;
        }

        public async Task<bool> WriteStreamAsBlobToStorageAsync(Stream content, string containerName,string filename){

            return await Task.FromResult(true);
        }

        public async Task<Stream> ReadFileFromBlobToStorageAsync(string containerName,string filename){

            if ( filename == _changeNumber)
                return await Task.FromResult((Stream)_input);
            else{
                MemoryStream stm=null;
                return await Task.FromResult(stm);
            }
                
        }

    }

    public class UnitTest1
    {

        private ServiceTagFile _ServiceTagFile = new ServiceTagFile();

                
        public UnitTest1()
        {
            _ServiceTagFile.ChangeNumber = "1";
            _ServiceTagFile.Cloud = "Azure";
            _ServiceTagFile.Id = "Id";
            _ServiceTagFile.Name = "File";
            _ServiceTagFile.Type = "Type";
            _ServiceTagFile.Values = new List<ServiceTagResult>();
        }

        private string Sz(){
            var options = new JsonSerializerOptions() {
                PropertyNameCaseInsensitive = true,
                IgnoreNullValues = true
            };
            return JsonSerializer.Serialize(_ServiceTagFile, typeof(ServiceTagFile), options);
        }

        private ServiceTagFile DSz(string json){
            
            var options = new JsonSerializerOptions() {
                PropertyNameCaseInsensitive = true,
                IgnoreNullValues = true
            };
            return JsonSerializer.Deserialize<ServiceTagFile>(json, options);
        }

        [Fact]
        public void Test_GetChangeNumber()
        {
            var changeNumber = AzureIpFileFunctions.GetChangeNumber(Sz());
            Assert.Equal(changeNumber,_ServiceTagFile.ChangeNumber);
        }

        [Fact]
        public void Test_GetPreviousFileAsync_NoPrevious(){
            
            // the blobClient in the real storage provider should return null if the blob.Exists() function is false

            IStorageProvider mockStorageProvider = new MockStorageProviderReadBlobDoesNotExist();
            var changeNumber = "3"; // needs to be valid
            var result =AzureIpFileFunctions.GetPreviousFileAsync(mockStorageProvider,"demo",changeNumber).Result;

            // no previous expect null
            Assert.Null(result);
        }

        [Fact]
        public void Test_GetPreviousFileAsync_Previous_Contiguous(){
            
            // the function should look for a file named previous change number, so here "2"
            var file  =new ServiceTagFile() { ChangeNumber = "3"};
            var fileAsStream = AzureIpFileFunctions.StringToStream( AzureIpFileFunctions.SerializeFile(file) );

            IStorageProvider mockStorageProvider = new MockStorageProviderReadBlobDoesExist(fileAsStream, "3");

            var changeNumber = "4"; // needs to be valid
            // expect file "3"
            var result = AzureIpFileFunctions.GetPreviousFileAsync(mockStorageProvider,"demo",changeNumber).Result;

            // found previous
            Assert.Equal(result.ChangeNumber,file.ChangeNumber);
        }

        [Fact]
        public void Test_GetPreviousFileAsync_Previous_NonContiguous(){
            
            // the function should look for a file named previous change number, even if there's a gap, so here "1"
            var file  =new ServiceTagFile() { ChangeNumber = "2"};
            var fileAsStream = AzureIpFileFunctions.StringToStream( AzureIpFileFunctions.SerializeFile(file) );

            IStorageProvider mockStorageProvider = new MockStorageProviderReadBlobDoesExist(fileAsStream, "2");

            var changeNumber = "4"; // needs to be valid
            // expect file "2"
            var result = AzureIpFileFunctions.GetPreviousFileAsync(mockStorageProvider,"demo",changeNumber).Result;

            // found previous
            Assert.Equal(result.ChangeNumber,file.ChangeNumber);
        }

               
        [Fact]
        public void Test_GetPreviousFileAsync_Previous_TooOld(){
            
            // the function should look for a file named previous change number, even if there's a gap, so here "1"
            var file  =new ServiceTagFile() { ChangeNumber = "1"};
            var fileAsStream = AzureIpFileFunctions.StringToStream( AzureIpFileFunctions.SerializeFile(file) );

            IStorageProvider mockStorageProvider = new MockStorageProviderReadBlobDoesExist(fileAsStream, "1");

            var changeNumber = "4"; // needs to be valid
            // expect file "1" - but implementation only goes back 2, so null
            var result = AzureIpFileFunctions.GetPreviousFileAsync(mockStorageProvider,"demo",changeNumber).Result;

            // found nothing
            Assert.Null(result);
        }

        [Fact]
        public void Test_CompareFiles_CurrentFileMissing(){

            Assert.Throws<Exception>(() => AzureIpFileFunctions.CompareFiles(null,null));
        }

        
        [Fact]
        public void Test_CompareFiles_PreviousMustHaveListValues(){
            var file  =new ServiceTagFile() { Values = new List<ServiceTagResult>() { new ServiceTagResult { Name = "Test"} }};
            Assert.Throws<Exception>(() => AzureIpFileFunctions.CompareFiles(file,new ServiceTagFile()));
        }


        [Fact]
        public void Test_CompareFiles_NoPrior_ExpectCurrent(){
            var file  =new ServiceTagFile() { Values = new List<ServiceTagResult>() { new ServiceTagResult { Name = "Test"} }};
            var delta=AzureIpFileFunctions.CompareFiles(file, null);
            Assert.Equal(delta.Count == 1 && delta[0].Name == "Test", true);
        }

        [Fact]
        public void Test_CompareFiles_DeltaIsLatest(){
            // old
            var prior = new ServiceTagFile() { Values= new List<ServiceTagResult>() };
            prior.Values.Add( new ServiceTagResult() {  Id= "a", Properties = new ServiceTagProperties() { ChangeNumber = "9"}});
            prior.Values.Add( new ServiceTagResult() {  Id= "b", Properties = new ServiceTagProperties() { ChangeNumber = "9"}});

            //newer
            var current  =new ServiceTagFile() { Values = new List<ServiceTagResult>() };
            current.Values.Add( new ServiceTagResult() {  Id= "a", Properties = new ServiceTagProperties() { ChangeNumber = "9"}});
            current.Values.Add( new ServiceTagResult() {  Id= "b", Properties = new ServiceTagProperties() { ChangeNumber = "10"}});
            current.Values.Add( new ServiceTagResult() {  Id= "c", Properties = new ServiceTagProperties() { ChangeNumber = "1"}});
            current.Values.Add( new ServiceTagResult() {  Id= "d", Properties = new ServiceTagProperties() { ChangeNumber = "2"}});

            var delta=AzureIpFileFunctions.CompareFiles(current, prior);

            // expect only result from current where the id matches and change number is > same id in prior
            // or the result entry is new
            Assert.Equal(delta.Count ,3);
            Assert.NotNull(delta.Single(r=> r.Id=="b" && r.Properties.ChangeNumber == "10"));
            Assert.NotNull(delta.Single(r=> r.Id=="c" && r.Properties.ChangeNumber == "1"));
            Assert.NotNull(delta.Single(r=> r.Id=="d" && r.Properties.ChangeNumber == "2"));
        }

        [Fact]
        public void Test_CompareChangeNumbers_false(){

            var current  =new ServiceTagResult() { Properties = new ServiceTagProperties() { ChangeNumber = "1" }};
            var prior  =new ServiceTagResult() { Properties = new ServiceTagProperties() { ChangeNumber = "1" }};

            var result=AzureIpFileFunctions.CompareChangeNumbers(current, prior);
            Assert.Equal(result,false);
        }

        [Fact]
        public void Test_CompareChangeNumbers_true(){

            var current  =new ServiceTagResult() { Properties = new ServiceTagProperties() { ChangeNumber = "2" }};
            var prior  =new ServiceTagResult() { Properties = new ServiceTagProperties() { ChangeNumber = "1" }};

            var result=AzureIpFileFunctions.CompareChangeNumbers(current, prior);
             Assert.Equal(result,true);
        }

    }
}
