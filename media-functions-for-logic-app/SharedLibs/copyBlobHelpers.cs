//
// Azure Media Services REST API v2 - Functions
//
// Shared Library
//

using System;
using System.Threading;
using System.IO;
using Microsoft.WindowsAzure.MediaServices.Client;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.Azure.WebJobs.Host;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace media_functions_for_logic_app
{
    public class CopyBlobHelpers
    {
        private const string ResourceId = "https://storage.azure.com/"; // Storage resource endpoint
        private const string AuthEndpoint = "https://login.microsoftonline.com/{0}/oauth2/token"; // Azure AD OAuth endpoint

        static string _storageAccountName = Environment.GetEnvironmentVariable("MediaServicesStorageAccountName");
        static string _storageAccountKey = Environment.GetEnvironmentVariable("MediaServicesStorageAccountKey");

        private static CloudStorageAccount _destinationStorageAccount = null;


        public class assetfileinJson
        {
            public string fileName = String.Empty;
            public bool isPrimary = false;
        }

        static public CloudBlobContainer GetCloudBlobContainer(string storageAccountName, string storageAccountKey, string containerName)
        {
            CloudStorageAccount sourceStorageAccount = new CloudStorageAccount(new StorageCredentials(storageAccountName, storageAccountKey), true);
            CloudBlobClient sourceCloudBlobClient = sourceStorageAccount.CreateCloudBlobClient();
            return sourceCloudBlobClient.GetContainerReference(containerName);
        }

        static public async void CopyBlobsAsync(CloudBlobContainer sourceBlobContainer, CloudBlobContainer destinationBlobContainer, TraceWriter log)
        {
            if (await destinationBlobContainer.CreateIfNotExistsAsync())
            {
                await destinationBlobContainer.SetPermissionsAsync(new BlobContainerPermissions
                {
                    PublicAccess = BlobContainerPublicAccessType.Blob
                });
            }

            string blobPrefix = null;
            bool useFlatBlobListing = true;

            List<IListBlobItem> blobList = new List<IListBlobItem>();
            BlobContinuationToken continuationToken = null;
            do
            {
                var responseList = await sourceBlobContainer.ListBlobsSegmentedAsync(blobPrefix, useFlatBlobListing, BlobListingDetails.None, null, continuationToken, null, null);
                continuationToken = responseList.ContinuationToken;
                blobList.AddRange(responseList.Results);
            }
            while (continuationToken != null);

            foreach (var sourceBlob in blobList)
            {
                log.Info("Source blob : " + (sourceBlob as CloudBlob).Uri.ToString());
                CloudBlob destinationBlob = destinationBlobContainer.GetBlockBlobReference((sourceBlob as CloudBlob).Name);
                if (await destinationBlob.ExistsAsync())
                {
                    log.Info("Destination blob already exists. Skipping: " + destinationBlob.Uri.ToString());
                }
                else
                {
                    log.Info("Copying blob " + sourceBlob.Uri.ToString() + " to " + destinationBlob.Uri.ToString());
                    CopyBlobAsync(sourceBlob as CloudBlob, destinationBlob);
                }
            }
        }

        static public async void CopyBlobAsync(CloudBlob sourceBlob, CloudBlob destinationBlob)
        {
            var signature = sourceBlob.GetSharedAccessSignature(new SharedAccessBlobPolicy
            {
                Permissions = SharedAccessBlobPermissions.Read,
                SharedAccessExpiryTime = DateTime.UtcNow.AddHours(24)
            });
            await destinationBlob.StartCopyAsync(new Uri(sourceBlob.Uri.AbsoluteUri + signature));
        }

        static public async Task<CopyStatus> MonitorBlobContainer(CloudBlobContainer destinationBlobContainer)
        {
            string blobPrefix = null;
            bool useFlatBlobListing = true;

            List<IListBlobItem> destBlobList = new List<IListBlobItem>();
            BlobContinuationToken continuationToken = null;
            do
            {
                var responseList = await destinationBlobContainer.ListBlobsSegmentedAsync(blobPrefix, useFlatBlobListing, BlobListingDetails.Copy, null, continuationToken, null, null);
                continuationToken = responseList.ContinuationToken;
                destBlobList.AddRange(responseList.Results);
            }
            while (continuationToken != null);

            CopyStatus copyStatus = CopyStatus.Success;
            foreach (var dest in destBlobList)
            {
                var destBlob = dest as CloudBlob;
                if (destBlob.CopyState.Status == CopyStatus.Aborted || destBlob.CopyState.Status == CopyStatus.Failed)
                {
                    // Log the copy status description for diagnostics and restart copy
                    await destBlob.StartCopyAsync(destBlob.CopyState.Source);
                    copyStatus = CopyStatus.Pending;
                }
                else if (destBlob.CopyState.Status == CopyStatus.Pending)
                {
                    // We need to continue waiting for this pending copy
                    // However, let us log copy state for diagnostics
                    copyStatus = CopyStatus.Pending;
                }
                // else we completed this pending copy
            }
            return copyStatus;
        }

        public static async Task CopyBlobsToTargetContainer(CloudBlobContainer sourceContainer, CloudBlobContainer targetContainer, TraceWriter log)
        {
            try
            {
                List<IListBlobItem> sourceBlobList = new List<IListBlobItem>();
                BlobContinuationToken continuationToken = null;
                do
                {
                    var responseList = await sourceContainer.ListBlobsSegmentedAsync(String.Empty, true, BlobListingDetails.None, null, continuationToken, null, null);
                    continuationToken = responseList.ContinuationToken;
                    sourceBlobList.AddRange(responseList.Results);
                }
                while (continuationToken != null);

                foreach (var blob in sourceBlobList)
                {
                    log.Info($"Blob URI: {blob.Uri}");
                    if (blob is CloudBlockBlob)
                    {
                        CloudBlockBlob sourceBlob = (CloudBlockBlob)blob;
                        log.Info($"Blob Name: {sourceBlob.Name}");
                        CloudBlockBlob targetBlob = targetContainer.GetBlockBlobReference(sourceBlob.Name);

                        using (var stream = await sourceBlob.OpenReadAsync())
                        {
                            await targetBlob.UploadFromStreamAsync(stream);
                        }
                        log.Info($"Copied: {sourceBlob.Name}");
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"ERROR copying blobs to target output: {ex.Message}");
            }
        }

        public static async Task<CloudBlockBlob> GetOutputBlob(CloudBlobContainer sourceContainer, string filter, TraceWriter log)
        {
            CloudBlockBlob outputBlob = null;

            try
            {
                List<IListBlobItem> sourceBlobList = new List<IListBlobItem>();
                BlobContinuationToken continuationToken = null;
                do
                {
                    var responseList = await sourceContainer.ListBlobsSegmentedAsync(filter, true, BlobListingDetails.None, null, continuationToken, null, null);
                    continuationToken = responseList.ContinuationToken;
                    sourceBlobList.AddRange(responseList.Results);
                }
                while (continuationToken != null);

                foreach (var blob in sourceBlobList)
                {
                    log.Info($"Blob URI: {blob.Uri}");
                    if (blob is CloudBlockBlob)
                    {
                        outputBlob = (CloudBlockBlob)blob;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Error($"Error getting output blob: {ex.Message}");
            }

            return outputBlob;
        }

        public static async Task<IAsset> CreateAssetFromBlob(CloudMediaContext context, CloudBlockBlob blob, string assetName, TraceWriter log)
        {
            IAsset newAsset = null;

            try
            {
                Task<IAsset> copyAssetTask = CopyBlobHelpers.CreateAssetFromBlobAsync(context, blob, assetName, log);
                newAsset = await copyAssetTask;
                log.Info($"Asset Copied : {newAsset.Id}");
            }
            catch (Exception ex)
            {
                log.Info("Copy Failed");
                log.Info($"ERROR : {ex.Message}");
                throw ex;
            }

            return newAsset;
        }

        /// <summary>
        /// Creates a new asset and copies blobs from the specifed storage account.
        /// </summary>
        /// <param name="blob">The specified blob.</param>
        /// <returns>The new asset.</returns>
        public static async Task<IAsset> CreateAssetFromBlobAsync(CloudMediaContext context, CloudBlockBlob blob, string assetName, TraceWriter log)
        {
            //Get a reference to the storage account that is associated with the Media Services account. 
            StorageCredentials mediaServicesStorageCredentials =
                new StorageCredentials(_storageAccountName, _storageAccountKey);
            _destinationStorageAccount = new CloudStorageAccount(mediaServicesStorageCredentials, false);

            // Create a new asset. 
            var asset = context.Assets.Create(blob.Name, AssetCreationOptions.None);
            log.Info($"Created new asset {asset.Name}");

            IAccessPolicy writePolicy = context.AccessPolicies.Create("writePolicy",
                TimeSpan.FromHours(4), AccessPermissions.Write);
            ILocator destinationLocator = context.Locators.CreateLocator(LocatorType.Sas, asset, writePolicy);
            CloudBlobClient destBlobStorage = _destinationStorageAccount.CreateCloudBlobClient();

            // Get the destination asset container reference
            string destinationContainerName = (new Uri(destinationLocator.Path)).Segments[1];
            CloudBlobContainer assetContainer = destBlobStorage.GetContainerReference(destinationContainerName);

            try
            {
                await assetContainer.CreateIfNotExistsAsync();
            }
            catch (Exception ex)
            {
                log.Error("ERROR:" + ex.Message);
            }

            log.Info("Created asset.");

            // Get hold of the destination blob
            CloudBlockBlob destinationBlob = assetContainer.GetBlockBlobReference(blob.Name);

            // Copy Blob
            try
            {
                using (var stream = await blob.OpenReadAsync())
                {
                    await destinationBlob.UploadFromStreamAsync(stream);
                }

                log.Info("Copy Complete.");

                var assetFile = asset.AssetFiles.Create(blob.Name);
                assetFile.ContentFileSize = blob.Properties.Length;
                //assetFile.MimeType = "video/mp4";
                assetFile.IsPrimary = true;
                assetFile.Update();
                asset.Update();
            }
            catch (Exception ex)
            {
                log.Error(ex.Message);
                log.Info(ex.StackTrace);
                log.Info("Copy Failed.");
                throw;
            }

            destinationLocator.Delete();
            writePolicy.Delete();

            return asset;
        }


        public static async Task<IAsset> CreateAssetFromBlobMultipleFiles(CloudMediaContext context, CloudBlockBlob blob, string assetName, TraceWriter log, List<assetfileinJson> assetfilesAsset)
        {
            IAsset newAsset = null;

            try
            {
                Task<IAsset> copyAssetTask = CreateAssetFromBlobMultipleFilesAsync(context, blob, assetName, log, assetfilesAsset);
                newAsset = await copyAssetTask;
                log.Info($"Asset Copied : {newAsset.Id}");
            }
            catch (Exception ex)
            {
                log.Info("Copy Failed");
                log.Info($"ERROR : {ex.Message}");
                throw ex;
            }

            return newAsset;
        }

        public static async Task CopyBlob(CloudBlockBlob source, CloudBlockBlob destination)
        {
            using (var stream = await source.OpenReadAsync())
            {
                await destination.UploadFromStreamAsync(stream);
            }

        }

        public static async Task<IAsset> CreateAssetFromBlobMultipleFilesAsync(CloudMediaContext context, CloudBlockBlob blob, string assetName, TraceWriter log, List<assetfileinJson> assetfilesAsset)
        {
            //Get a reference to the storage account that is associated with the Media Services account. 
            StorageCredentials mediaServicesStorageCredentials =
                new StorageCredentials(_storageAccountName, _storageAccountKey);
            _destinationStorageAccount = new CloudStorageAccount(mediaServicesStorageCredentials, false);

            // Create a new asset. 
            var asset = context.Assets.Create(Path.GetFileNameWithoutExtension(blob.Name), AssetCreationOptions.None);
            log.Info($"Created new asset {asset.Name}");

            IAccessPolicy writePolicy = context.AccessPolicies.Create("writePolicy",
                TimeSpan.FromHours(4), AccessPermissions.Write);
            ILocator destinationLocator = context.Locators.CreateLocator(LocatorType.Sas, asset, writePolicy);
            CloudBlobClient destBlobStorage = _destinationStorageAccount.CreateCloudBlobClient();

            // Get the destination asset container reference
            string destinationContainerName = (new Uri(destinationLocator.Path)).Segments[1];
            CloudBlobContainer assetContainer = destBlobStorage.GetContainerReference(destinationContainerName);

            try
            {
                await assetContainer.CreateIfNotExistsAsync();
            }
            catch (Exception ex)
            {
                log.Error("ERROR:" + ex.Message);
            }

            log.Info("Created asset.");

            var sourceBlobContainer = blob.Container;

            foreach (var file in assetfilesAsset)
            {
                int nbtry = 0;
                var sourceBlob = sourceBlobContainer.GetBlobReference(file.fileName);

                while (!await sourceBlob.ExistsAsync() && nbtry < 86400) // let's wait 24 hours max
                {
                    log.Info("File " + file.fileName + " does not exist... waiting...");
                    Thread.Sleep(1000); // let's wait for the blob to be there
                    nbtry++;
                }
                if (nbtry == 86440)
                {
                    log.Info("File " + file.fileName + " does not exist... File asset transfer canceled.");
                    break;
                }

                log.Info("File " + file.fileName + " found.");

                // Copy Blob
                try
                {
                    // Get hold of the destination blob
                    CloudBlockBlob destinationBlob = assetContainer.GetBlockBlobReference(file.fileName);

                    using (var stream = await sourceBlob.OpenReadAsync())
                    {
                        await destinationBlob.UploadFromStreamAsync(stream);
                    }

                    log.Info("Copy Complete.");

                    var assetFile = asset.AssetFiles.Create(sourceBlob.Name);
                    assetFile.ContentFileSize = sourceBlob.Properties.Length;
                    //assetFile.MimeType = "video/mp4";
                    if (file.isPrimary)
                    {
                        assetFile.IsPrimary = true;
                    }
                    assetFile.Update();
                    asset.Update();
                }
                catch (Exception ex)
                {
                    log.Error(ex.Message);
                    log.Info(ex.StackTrace);
                    log.Info("Copy Failed.");
                    throw;
                }
            }


            destinationLocator.Delete();
            writePolicy.Delete();

            return asset;
        }

        static public async Task<List<CloudBlob>> CopyFilesAsync(CloudBlobContainer sourceBlobContainer, CloudBlobContainer destinationBlobContainer, string prefix, string extension, TraceWriter log)
        {
            // init the list of tasks
            List<CloudBlob> mylistresults = new List<CloudBlob>();

            if (destinationBlobContainer.CreateIfNotExists())
            {
                destinationBlobContainer.SetPermissions(new BlobContainerPermissions
                {
                    PublicAccess = BlobContainerPublicAccessType.Container // read-only access to container
                });
            }

            string blobPrefix = null;
            bool useFlatBlobListing = true;
            long size = 0;

            var blobList = sourceBlobContainer.ListBlobs(blobPrefix, useFlatBlobListing, BlobListingDetails.None);
            foreach (var sourceBlob in blobList)
            {
                if ((sourceBlob as CloudBlob).Name.EndsWith("." + extension))
                {
                    log.Info("Source blob : " + (sourceBlob as CloudBlob).Uri.ToString());
                    CloudBlob destinationBlob = destinationBlobContainer.GetBlockBlobReference(prefix + (sourceBlob as CloudBlob).Name);
                    if (destinationBlob.Exists())
                    {
                        log.Info("Destination blob already exists. Skipping: " + destinationBlob.Uri.ToString());
                    }
                    else
                    {
                        log.Info("Copying blob " + sourceBlob.Uri.ToString() + " to " + destinationBlob.Uri.ToString());
                        size = (sourceBlob as CloudBlob).Properties.Length;
                        log.Info("Source Blob size: " + size.ToString());
                        //mylistresults.Add(CopyBlobAsync(sourceBlob as CloudBlob, destinationBlob));

                        var signature = (sourceBlob as CloudBlob).GetSharedAccessSignature(new SharedAccessBlobPolicy
                        {
                            Permissions = SharedAccessBlobPermissions.Read,
                            SharedAccessExpiryTime = DateTime.UtcNow.AddHours(24)
                        });

                        //mylistresults.Add(destinationBlob.StartCopyAsync(new Uri(sourceBlob.Uri.AbsoluteUri + signature)));
                        await destinationBlob.StartCopyAsync(new Uri(sourceBlob.Uri.AbsoluteUri + signature));

                        mylistresults.Add(destinationBlob);

                    }
                }
            }

            return mylistresults;
        }
    }


}






