using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training;
using System.Linq;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;

namespace CustomVisionHelper
{
    public class CustomVisionIterationHelper
    {
        private static HttpClient httpClient = new HttpClient();

        private CustomVisionTrainingClientHelper _clientHelper = null;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="clientHelper"></param>
        public CustomVisionIterationHelper(CustomVisionTrainingClientHelper clientHelper)
        {
            _clientHelper = clientHelper;
        }

        public string GetIterationExportUrl()
        {
            string downloadUrl = null;

            CustomVisionIterationDetails serviceDetails = _clientHelper.ServiceDetails as CustomVisionIterationDetails;

            if (serviceDetails != null)
            {
                if (serviceDetails.IsValidIteration())
                {
                    var iterations = _clientHelper.Client.GetIterations(serviceDetails.ProjectId);

                    var iteration = (from i in iterations
                                     where i.Name == serviceDetails.IterationName
                                     select i).FirstOrDefault();

                    var exports = _clientHelper.Client.GetExports(iteration.ProjectId, iteration.Id);

                    //loop to find the export, if not present at all then call the Export Iteration method to start the export process

                    var myExport = (from e in exports
                                    where e.Platform == Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models.ExportPlatform.DockerFile
                                        &&
                                        e.Flavor == Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models.ExportFlavor.Linux
                                    select e).FirstOrDefault();

                    if (myExport == null)
                    {
                        //ok then start export...
                        var export = _clientHelper.Client.ExportIteration(iteration.ProjectId, iteration.Id, 
                            ExportPlatform.DockerFile, ExportFlavor.Linux);
                    }

                    bool exportReady = false;


                    ///TODO: make this configurable
                    int retryNumber = 10;

                    while ( !exportReady && retryNumber> 0)
                    {
                        retryNumber--;

                        myExport = (from e in exports
                                        where e.Platform == Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models.ExportPlatform.DockerFile
                                            &&
                                            e.Flavor == Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models.ExportFlavor.Linux
                                        select e).FirstOrDefault();

                        if (myExport.Status != ExportStatus.Done)
                        {
                            //ok just wait 30 seconds more
                            Task.Delay(30000).Wait();
                        }
                    }

                    if (myExport != null)
                    {
                        if (myExport.Status == ExportStatus.Done)
                        {
                            downloadUrl = myExport.DownloadUri;
                        }
                        else
                        {
                            throw new ApplicationException($"Error while exporting Custom Vision Module: Export Status: {myExport.Status}");
                        }
                    }
                    else
                    {
                        throw new ApplicationException($"Error while exporting Custom Vision Module: Export not found.");
                    }
                }
            }
            
            return downloadUrl;
        }

        public byte[] DownloadExportZip(string downloadUrl)
        {
            byte[] result =  httpClient.GetByteArrayAsync(downloadUrl).Result;

            return result;
        }
    }
}
