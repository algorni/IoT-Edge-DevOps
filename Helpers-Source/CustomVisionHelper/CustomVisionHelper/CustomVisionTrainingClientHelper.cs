using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace CustomVisionHelper
{
    public class CustomVisionTrainingClientHelper
    {
        private CustomVisionTrainingDetails _serviceDetails = null;

        private CustomVisionTrainingClient _customVisionTrainingClient;

        private ILogger _logger = null;

        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="serviceDetails"></param>
        public CustomVisionTrainingClientHelper(IConfiguration configuration, ILogger logger, bool includeIteration = false)
        {
            _logger = logger;

            LoggerHelper.LogTrace(_logger, "Parsing Custom Vision Training service details from configuration.");

            if (!includeIteration)
            {
                _serviceDetails = new CustomVisionTrainingDetails();
            }
            else
            {
                _serviceDetails = new CustomVisionIterationDetails();
            }
            
            //and check also if the Custom Vision training endpoint details are provided
            _serviceDetails.ApiKey = configuration["CUSTOM_VISION_TRAINING_ApiKey"];
            _serviceDetails.EndPoint = configuration["CUSTOM_VISION_TRAINING_EndPoint"];
            var projectIdStr = configuration["CUSTOM_VISION_TRAINING_ProjectId"];
            Guid projectId;
            var projectIdParsed = Guid.TryParse(projectIdStr, out projectId);
            if (projectIdParsed)
                _serviceDetails.ProjectId = projectId;

            if (includeIteration)
            {
                (_serviceDetails as CustomVisionIterationDetails).IterationName = configuration["CUSTOM_VISION_TRAINING_IterationName"];
            }


            if (_serviceDetails.IsValid())
            {
                LoggerHelper.LogTrace(_logger, "Creating CustomVisionTrainingClient.");

                _customVisionTrainingClient =
                    new Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.CustomVisionTrainingClient()
                    {
                        ApiKey = _serviceDetails.ApiKey,
                        Endpoint = _serviceDetails.EndPoint
                    };
            }
            else
            {
                LoggerHelper.LogError(_logger, "Impossible to CustomVisionTrainingClient, no service details found in the configuration.");
            }
        }

        public CustomVisionTrainingClient Client
        {
            get { return _customVisionTrainingClient; }
        }

        public CustomVisionTrainingDetails ServiceDetails
        {
            get { return _serviceDetails; }
        }
    }
}
