using CustomVisionHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IterationExporter
{
    class Program
    {
        static IConfiguration configuration;
        static ILogger<Program> logger = null;

        static void Main(string[] args)
        {
            Console.WriteLine("Custom Vision Iteration Exporter");

            var builder = new ConfigurationBuilder()
           .SetBasePath(Directory.GetCurrentDirectory())
           .AddJsonFile("appsettings.json", optional: true)
           .AddEnvironmentVariables();

            configuration = builder.Build();

            var serviceCollection = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            ConfigureServices(serviceCollection);

            var serviceProvider = serviceCollection.BuildServiceProvider();

            logger = serviceProvider.GetService<ILogger<Program>>();

            string targetPath = configuration["targetPath"].ToString();
            LoggerHelper.LogTrace(logger, $"TargetPath for extracting the Docker module is: {targetPath}");

            string[] ignoreFileList = null;
            string ignoreFileListCsv = configuration["ignoreFileList"];
            ignoreFileList = ignoreFileListCsv.Split(',');
            

            LoggerHelper.LogTrace(logger, $"IgnoreFile has # of Items: {ignoreFileList.Count()}");


            CustomVisionTrainingClientHelper customVisionTrainingClientHelper = 
                new CustomVisionTrainingClientHelper(configuration, logger, true);

            if (customVisionTrainingClientHelper.Client == null)
            {
                LoggerHelper.LogError(logger, $"CV Client issue");
                return;
            }
            else
            {
                CustomVisionIterationHelper iterationHelper = new CustomVisionIterationHelper(customVisionTrainingClientHelper);

                string exportUrl = null;

                try
                {
                    exportUrl = iterationHelper.GetIterationExportUrl();
                }
                catch (Exception ex)
                {
                    LoggerHelper.LogError(logger, $"Issue geting Iteration Export URL.\n{ex.ToString()}");
                }

                if (!string.IsNullOrEmpty(exportUrl))
                {
                    byte[] exportZip = iterationHelper.DownloadExportZip(exportUrl);

                    string tempPath = Path.GetTempPath();

                    string zipFilePath = Path.Combine(tempPath, "iteration.zip");

                    File.WriteAllBytes(zipFilePath, exportZip);

                    string tempExtractFolder = Path.Combine(tempPath, "tempExtract");

                    DirectoryInfo tempExtractFolderInfo = new DirectoryInfo(tempExtractFolder);
                    if (tempExtractFolderInfo.Exists)
                    {
                        tempExtractFolderInfo.Delete(true);
                    }
                    
                    System.IO.Compression.ZipFile.ExtractToDirectory(zipFilePath, tempExtractFolder);

                    var directoryInfo = new DirectoryInfo(tempExtractFolder);

                    IEnumerable<string> fileBlackList = from fi in directoryInfo.EnumerateFiles("*", SearchOption.AllDirectories)
                                                        where ignoreFileList.Any(ignore => fi.FullName.EndsWith(new FileInfo(Path.Combine(tempExtractFolder,ignore)).FullName))
                                                        select fi.FullName;

                    foreach (var fileToDelete in fileBlackList)
                    {
                        File.Delete(fileToDelete);
                    }

                    MoveDirectory(tempExtractFolder, targetPath);

                    //delete temp stuff when done
                    File.Delete(zipFilePath);
                }
                else
                {
                    LoggerHelper.LogError(logger, $"Issue geting Iteration Export URL.");
                }
                
            }
        }

        public static void MoveDirectory(string source, string target)
        {
            var sourcePath = source.TrimEnd('\\', ' ');
            var targetPath = target.TrimEnd('\\', ' ');
            var files = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories)
                                 .GroupBy(s => Path.GetDirectoryName(s));
            foreach (var folder in files)
            {
                var targetFolder = folder.Key.Replace(sourcePath, targetPath);
                Directory.CreateDirectory(targetFolder);
                foreach (var file in folder)
                {
                    var targetFile = Path.Combine(targetFolder, Path.GetFileName(file));
                    if (File.Exists(targetFile)) File.Delete(targetFile);
                    File.Move(file, targetFile);
                }
            }
            Directory.Delete(source, true);
        }

        private static void ConfigureServices(IServiceCollection services)
        {
            services.AddLogging(configure => configure.AddConsole());
        }
    }
}
