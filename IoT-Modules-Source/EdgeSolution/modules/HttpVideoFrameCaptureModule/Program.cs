namespace HttpVideoFrameCaptureModule
{
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
    using Microsoft.Azure.Devices.Client;
    using Microsoft.Azure.Devices.Shared;
    using Microsoft.Extensions.Configuration;
    using Newtonsoft.Json;

    class Program
    {
        static IConfiguration configuration;

        static string IMAGE_PROCESSING_ENDPOINT;
        static string IMAGE_SOURCE_URL;
        static TimeSpan IMAGE_POLLING_INTERVAL = TimeSpan.FromSeconds(10.0); //default 10 sconds
        static string MODE;

        static int loopNumber = 0;
        static bool reportingLoop = false;
        static int reportingLoopInterval = 15;

        static HttpClient httpClient = new HttpClient();


        static void Main(string[] args)
        {
            Console.WriteLine("httpVideoFrameCaptureModule module starting...");

            var builder = new ConfigurationBuilder()
           .SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json", optional: true)
           .AddEnvironmentVariables();

            configuration = builder.Build();

            Console.WriteLine("Configuration loaded");

            //Get the Module mode
            //MODE = configuration["MODE"].ToString();

            //Getting the IMAGE PROCESSING ENDOPOINT (the Custom Vision Module trained)
            IMAGE_PROCESSING_ENDPOINT = configuration["IMAGE_PROCESSING_ENDPOINT"].ToString();

            //The url of the image source
            IMAGE_SOURCE_URL = configuration["IMAGE_SOURCE_URL"].ToString();
            
            var IMAGE_POLLING_INTERVAL_string = configuration["IMAGE_POLLING_INTERVAL"].ToString();
            TimeSpan.TryParse(IMAGE_POLLING_INTERVAL_string, out IMAGE_POLLING_INTERVAL);

            Console.WriteLine($"IMAGE_PROCESSING_ENDPOINT:{IMAGE_PROCESSING_ENDPOINT}");
            Console.WriteLine($"IMAGE_SOURCE_URL:{IMAGE_SOURCE_URL}");
            Console.WriteLine($"IMAGE_POLLING_INTERVAL:{IMAGE_POLLING_INTERVAL_string}");

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
                    throw new InvalidOperationException("UserContext doesn't contain " + "expected values");
                }

                //used for polling calcualtion
                DateTime startTime = DateTime.UtcNow;

                //get the image
                byte[] imageBytes = getImage(moduleClient);

                if (imageBytes != null)
                {
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

                TimeSpan loopDuration = DateTime.UtcNow - startTime;

                TimeSpan timeToNextRun = IMAGE_POLLING_INTERVAL - loopDuration;

                if (timeToNextRun > TimeSpan.FromSeconds(0.0))
                {
                    Task.Delay((int)timeToNextRun.TotalMilliseconds);
                }
            }
        }

        
    
        private static byte[] getImage(ModuleClient moduleClient)
        {
            byte[] imageByte = null;

            //used for latency calcualtion
            DateTime startTime = DateTime.UtcNow;

            //download the image from the URL
            HttpResponseMessage httpResponse = null;

            try
            {
                httpResponse = httpClient.GetAsync(IMAGE_SOURCE_URL).Result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling IMAGE SOURCE URL: {IMAGE_SOURCE_URL}\n{ex.ToString()}");

                return null;
            }

            if (httpResponse.IsSuccessStatusCode)
            {
                bool isImage = isImageContentType(httpResponse.Content.Headers.ContentType);

                if (isImage)
                {
                    imageByte = httpResponse.Content.ReadAsByteArrayAsync().Result;
                }
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
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error calling IMAGE_PROCESSING_ENDPOINT: {IMAGE_PROCESSING_ENDPOINT}\n{ex.ToString()}");

                return null;
            }
            
            if (httpResponse.IsSuccessStatusCode)
            {
                string predictionResponseString = httpResponse.Content.ReadAsStringAsync().Result;

                //parse as JSON
                predictionResponse = JsonConvert.DeserializeObject<PredictionResponse>(predictionResponseString);
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
            message.Properties.Add("MSG","PredictionResponse");

            moduleClient.SendEventAsync(message).Wait();
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
            
            return Task.CompletedTask;
        }
    }
}
