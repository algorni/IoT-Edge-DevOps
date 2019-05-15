using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace HttpVideoFrameCaptureModule
{
    class Program
    {
        static IConfiguration configuration;

        static string IMAGE_PROCESSING_ENDPOINT;
        static string IMAGE_SOURCE_URL;
        static IPCameraCredential IP_CAMERA_CREDENTIAL = new IPCameraCredential();
        static TimeSpan IMAGE_POLLING_INTERVAL = TimeSpan.FromSeconds(60.0); //default 60 sconds
        static int loopNumber = 0;
        static bool reportingLoop = false;
        static int reportingLoopInterval = 15;

        static HttpClient httpClient = new HttpClient();

        static OperatingMode MODE = OperatingMode.ImageClassification;

        static CustomVisionTrainingDetails CUSTOM_VISION_TRAINING = new CustomVisionTrainingDetails();

        static CustomVisionTrainingClient customVisionTrainingClient = null;

        static void Main(string[] args)
        {
            Console.WriteLine("httpVideoFrameCaptureModule module starting...");

            var builder = new ConfigurationBuilder()
           .SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json", optional: true)
           .AddEnvironmentVariables();

            configuration = builder.Build();

            Console.WriteLine("Configuration loaded");

            //Getting the IMAGE PROCESSING ENDOPOINT (the Custom Vision Module trained)
            IMAGE_PROCESSING_ENDPOINT = configuration["IMAGE_PROCESSING_ENDPOINT"].ToString();

            //The url of the image source
            IMAGE_SOURCE_URL = configuration["IMAGE_SOURCE_URL"].ToString();

            var IMAGE_POLLING_INTERVAL_string = configuration["IMAGE_POLLING_INTERVAL"].ToString();
            int IMAGE_POLLING_INTERVAL_seconds = 60;
            var pollingIntervalParsed = int.TryParse(IMAGE_POLLING_INTERVAL_string, out IMAGE_POLLING_INTERVAL_seconds);
            IMAGE_POLLING_INTERVAL = TimeSpan.FromSeconds(IMAGE_POLLING_INTERVAL_seconds);

            //check also for the MODE (we can spin up the module just for training purpose)
            string modeStr = configuration["MODE"] as string;
            if (!string.IsNullOrEmpty(modeStr))
            {
                var updated = Enum.TryParse<OperatingMode>(modeStr, out MODE);
                Console.WriteLine($"OperatingMode: {MODE} - Parsed: {updated}");
            }

            //and check also if the Custom Vision training endpoint details are provided
            CUSTOM_VISION_TRAINING.ApiKey = configuration["CUSTOM_VISION_TRAINING_ApiKey"];   
            CUSTOM_VISION_TRAINING.EndPoint = configuration["CUSTOM_VISION_TRAINING_EndPoint"]; 
            var projectIdStr = configuration["CUSTOM_VISION_TRAINING_ProjectId"];   
            Guid projectId;        
            var projectIdParsed = Guid.TryParse(projectIdStr, out projectId);
            if ( projectIdParsed)
                CUSTOM_VISION_TRAINING.ProjectId = projectId;

            IP_CAMERA_CREDENTIAL.UserName = configuration["IP_CAMERA_CREDENTIAL_UserName"];   
            IP_CAMERA_CREDENTIAL.Password = configuration["IP_CAMERA_CREDENTIAL_Password"]; 

            Console.WriteLine($"IMAGE_PROCESSING_ENDPOINT:{IMAGE_PROCESSING_ENDPOINT}");
            Console.WriteLine($"IMAGE_SOURCE_URL:{IMAGE_SOURCE_URL}");
            Console.WriteLine($"IMAGE_POLLING_INTERVAL:{IMAGE_POLLING_INTERVAL.ToString()} - Parsed: {pollingIntervalParsed}");
            
            Console.WriteLine($"MODE:{MODE}");

            Console.WriteLine($"CUSTOM_VISION_TRAINING_ApiKey:{CUSTOM_VISION_TRAINING.ApiKey}");
            Console.WriteLine($"CUSTOM_VISION_TRAINING_EndPoint:{CUSTOM_VISION_TRAINING.EndPoint}");
            Console.WriteLine($"CUSTOM_VISION_TRAINING_ProjectId:{CUSTOM_VISION_TRAINING.ProjectId}");

            Console.WriteLine($"IP_CAMERA_CREDENTIAL_UserName:{IP_CAMERA_CREDENTIAL.UserName}");
            Console.WriteLine($"IP_CAMERA_CREDENTIAL_Password:{IP_CAMERA_CREDENTIAL.Password}");

            Init().Wait();

            // Wait until the app unloads or is cancelled
            var cts = new CancellationTokenSource();
            AssemblyLoadContext.Default.Unloading += (ctx) => cts.Cancel();
            Console.CancelKeyPress += (sender, cpe) => cts.Cancel();
            WhenCancelled(cts.Token).Wait();
        }

        /// <summary>
        /// Handles cleanup operations when app is cancelled or unloads
        /// </summary>
        public static Task WhenCancelled(CancellationToken cancellationToken)
        {
            var tcs = new TaskCompletionSource<bool>();
            cancellationToken.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tcs);
            return tcs.Task;
        }

        /// <summary>
        /// Initializes the ModuleClient and sets up the callback to receive
        /// messages containing temperature information
        /// </summary>
        static async Task Init()
        {
            AmqpTransportSettings amqpSetting = new AmqpTransportSettings(TransportType.Amqp_Tcp_Only);
            ITransportSettings[] settings = { amqpSetting };

            // Open a connection to the Edge runtime
            ModuleClient ioTHubModuleClient = await ModuleClient.CreateFromEnvironmentAsync(settings);
            await ioTHubModuleClient.OpenAsync();
            Console.WriteLine("IoT Hub module Client initialized.");

            // Execute callback function during Init for Twin desired properties
            var twin = await ioTHubModuleClient.GetTwinAsync();
            await onDesiredPropertiesUpdate(twin.Properties.Desired, ioTHubModuleClient);

            //Register the desired property callback
            await ioTHubModuleClient.SetDesiredPropertyUpdateCallbackAsync(onDesiredPropertiesUpdate, ioTHubModuleClient);

            //start the main thread that will do the real job of the module
            var thread = new Thread(() => mainThreadBody(ioTHubModuleClient));
            thread.Start();
        }


        private static void mainThreadBody(object userContext)
        {
            Console.WriteLine("Entering main thread");

            while (true)
            {
                if (loopNumber == reportingLoopInterval)
                {
                    Console.WriteLine("In this loop performance will be reported.");
                    reportingLoop = true;
                    loopNumber = 0;
                }
                else
                {
                    reportingLoop = false;
                }

                var moduleClient = userContext as ModuleClient;

                if (moduleClient == null)
                {
                    Console.WriteLine("Module Client is NULL.");
                    throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
                }

                //used for polling calcualtion
                DateTime startTime = DateTime.UtcNow;

                //get the image
                byte[] imageBytes = getImage(moduleClient);

                if (imageBytes != null)
                {
                    Console.WriteLine("Image Bytes not null... good job.");

                    if (MODE == OperatingMode.ImageClassification)
                    {
                        Console.WriteLine("Calling Image Classification");

                        //call the Custom Vision Module 
                        PredictionResponse predictionResponse = invokePrediction(imageBytes, moduleClient);

                        if (predictionResponse != null)
                        {
                            //publish the response message as output
                            publishPredictionResponse(predictionResponse, moduleClient);

                            TimeSpan endToEndDuration = DateTime.UtcNow - startTime;

                            if (reportingLoop)
                            {
                                reportLatency("END_TO_END", endToEndDuration, moduleClient);
                            }

                            loopNumber++;
                        }
                    }

                    if ( CUSTOM_VISION_TRAINING.IsValid())
                    {
                        Console.WriteLine("CUSTOM_VISION_TRAINING.IsValid()");
                    }
                    else
                    {
                        Console.WriteLine("NOT CUSTOM_VISION_TRAINING.IsValid()");
                    }

                    if ((MODE == OperatingMode.TrainingToCloud) && CUSTOM_VISION_TRAINING.IsValid())
                    {
                        Console.WriteLine("OperatingMode TrainingToCloud and Custom Vision training data is provided");

                        if (customVisionTrainingClient == null)
                        {
                            Console.WriteLine("Creating CustomVisionTrainingClient.");

                            customVisionTrainingClient =
                                new Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.CustomVisionTrainingClient()
                                {
                                    ApiKey = CUSTOM_VISION_TRAINING.ApiKey,
                                    Endpoint = CUSTOM_VISION_TRAINING.EndPoint
                                };
                        }

                        //ok now just send an image to Custom Vision for training purpose 
                        MemoryStream imageMemoryStream = new MemoryStream(imageBytes);

                        Console.WriteLine("Uploading the image into Custom Vision ");
                        Microsoft.Azure.CognitiveServices.Vision.CustomVision.Training.Models.ImageCreateSummary imageCreateSummary =   
                            customVisionTrainingClient.CreateImagesFromData(CUSTOM_VISION_TRAINING.ProjectId, imageMemoryStream);

                        Console.WriteLine($"Success: {imageCreateSummary.IsBatchSuccessful}");
                    }
                }

                Console.WriteLine("Calculating time to next run");

                TimeSpan loopDuration = DateTime.UtcNow - startTime;

                TimeSpan timeToNextRun = IMAGE_POLLING_INTERVAL - loopDuration;

                Console.WriteLine($"Time to next run: {timeToNextRun.ToString()}");

                if (timeToNextRun > TimeSpan.FromSeconds(0.0))
                {
                    Console.WriteLine("Delay");

                    Task.Delay((int)timeToNextRun.TotalMilliseconds).Wait();
                }
            }
        }



        private static byte[] getImage(ModuleClient moduleClient)
        {
            Console.WriteLine("Getting Image");

            byte[] imageByte = null;

            //used for latency calcualtion
            DateTime startTime = DateTime.UtcNow;

            //download the image from the URL
            HttpResponseMessage httpResponse = null;

            try
            {
                if ( IP_CAMERA_CREDENTIAL.IsValid())
                {
                    string credentialToEncode = $"{IP_CAMERA_CREDENTIAL.UserName}:{IP_CAMERA_CREDENTIAL.Password}";

                    Console.WriteLine($"Using Credentials: {credentialToEncode}");

                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                        "Basic", Convert.ToBase64String(System.Text.ASCIIEncoding.ASCII.GetBytes(credentialToEncode)));
                }

                httpResponse = httpClient.GetAsync(IMAGE_SOURCE_URL).Result;

                Console.WriteLine("HTTP call executed.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling IMAGE SOURCE URL: {IMAGE_SOURCE_URL}\n{ex.ToString()}");

                return null;
            }

            try
            {
                if (httpResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"HTTP call IsSuccessStatusCode: {httpResponse.StatusCode}");

                    bool isImage = isImageContentType(httpResponse.Content.Headers.ContentType);

                    if (isImage)
                    {
                        Console.WriteLine("Is an Image");

                        imageByte = httpResponse.Content.ReadAsByteArrayAsync().Result;

                        Console.WriteLine("Got bytes");
                    }
                }
                else
                {
                    Console.WriteLine($"HTTP return code is NOT Success Status Code: {httpResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error evaluating HTTP result\n{ex.ToString()}");

                return null;
            }

            TimeSpan endToEndDuration = DateTime.UtcNow - startTime;

            if (reportingLoop)
            {
                reportLatency("GET_IMAGE", endToEndDuration, moduleClient);
            }

            return imageByte;
        }

        private static bool isImageContentType(MediaTypeHeaderValue contentType)
        {
            if (contentType.MediaType.ToLower().Contains("image"))
                return true;
            else
                return false;
        }

        private static PredictionResponse invokePrediction(byte[] imageBytes, ModuleClient moduleClient)
        {
            PredictionResponse predictionResponse = null;

            //used for latency calcualtion
            DateTime startTime = DateTime.UtcNow;

            ByteArrayContent imageContent = new ByteArrayContent(imageBytes);
            imageContent.Headers.Add("Content-Type", "application/octet-stream");

            HttpResponseMessage httpResponse = null;

            try
            {
                httpResponse = httpClient.PostAsync(IMAGE_PROCESSING_ENDPOINT, imageContent).Result;

                Console.WriteLine("Image Classification endpoint called.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling IMAGE_PROCESSING_ENDPOINT: {IMAGE_PROCESSING_ENDPOINT}\n{ex.ToString()}");

                return null;
            }

            try
            {
                if (httpResponse.IsSuccessStatusCode)
                {
                    Console.WriteLine($"HTTP call IsSuccessStatusCode: {httpResponse.StatusCode}");

                    string predictionResponseString = httpResponse.Content.ReadAsStringAsync().Result;

                    //parse as JSON
                    predictionResponse = JsonConvert.DeserializeObject<PredictionResponse>(predictionResponseString);
                }
                else
                {
                    Console.WriteLine($"HTTP return code is NOT Success Status Code: {httpResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error evaluating HTTP result\n{ex.ToString()}");

                return null;
            }

            TimeSpan endToEndDuration = DateTime.UtcNow - startTime;

            if (predictionResponse != null && reportingLoop)
            {
                reportLatency("INVOKE_PREDICTOR", endToEndDuration, moduleClient);
            }

            return predictionResponse;
        }

        private static void publishPredictionResponse(PredictionResponse predictionResponse, ModuleClient moduleClient)
        {
            string predictionString = JsonConvert.SerializeObject(predictionResponse);

            byte[] messageContent = System.Text.Encoding.UTF8.GetBytes(predictionString);

            Message message = new Message(messageContent);
            message.ContentType = "application/json";
            message.Properties.Add("MSG", "PredictionResponse");

            Console.WriteLine("Publishing Prediction Result to Module Output: predictions...");

            moduleClient.SendEventAsync("predictions", message).Wait();

            Console.WriteLine("Publishing Prediction Result to Module Output done");
        }


        private static void reportLatency(string taskName, TimeSpan endToEndDuration, ModuleClient moduleClient)
        {
            TwinCollection reportedProperties = new TwinCollection();
            reportedProperties[$"LATENCY_{taskName}"] = endToEndDuration.TotalMilliseconds;

            moduleClient.UpdateReportedPropertiesAsync(reportedProperties);
        }

        private static Task onDesiredPropertiesUpdate(TwinCollection desiredProperties, object userContext)
        {
            var moduleClient = userContext as ModuleClient;
            if (moduleClient == null)
            {
                throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
            }

            if (desiredProperties.Contains("IMAGE_POLLING_INTERVAL"))
            {
                //update the Polling Interval (if possible)
                var updated = TimeSpan.TryParse(desiredProperties["IMAGE_POLLING_INTERVAL"], out IMAGE_POLLING_INTERVAL);

                if (updated)
                {
                    Console.WriteLine("IMAGE_POLLING_INTERVAL Updated.");

                    moduleClient.UpdateReportedPropertiesAsync(desiredProperties);
                }
            }

            if (desiredProperties.Contains("MODE"))
            {
                //update the MODE
                var updated = Enum.TryParse<OperatingMode>(desiredProperties["MODE"], out MODE);

                if (updated)
                {
                    Console.WriteLine("MODE Updated.");

                    moduleClient.UpdateReportedPropertiesAsync(desiredProperties);
                }
            }

            return Task.CompletedTask;
        }
    }
}
