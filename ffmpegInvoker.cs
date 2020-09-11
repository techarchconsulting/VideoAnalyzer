using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Newtonsoft.Json;
using SixLabors.ImageSharp.Formats.Png;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;

namespace VideoEncoder
{
    public static class ffmpegInvoker
    {
         

        [FunctionName("ffmpegInvoker")]
        public static void Run([BlobTrigger("inbound/{name}", Connection = "BlobPath")] Stream myBlob,
                      string name,
                        ExecutionContext context, ILogger log)

        {
            log.LogInformation($"C# Blob trigger function Processed blob\n Name:ffmeg-encoding \n Size: {myBlob.Length} Bytes");

            var config = new ConfigurationBuilder()
                .SetBasePath(context.FunctionAppDirectory)
                .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true) // <- This gives you access to your application settings in your local development environment
                .AddEnvironmentVariables() // <- This is what actually gets you the application settings in Azure
                .Build();
            string outPath = config["OutPath"];
            string apiUrl  = config["PredictionApi"];
            string key  = config["PredictionApiKey"];

            ProcessLog RunResult = new ProcessLog();
            ResultMetaData result = new ResultMetaData();
            result.FileName = name;
            RunResult.OverallStatus = ResultStatus.Started;
            RunResult.StageResults = new System.Collections.Generic.List<Stage>();

            var folder = context.FunctionAppDirectory;
            var tempFolder = Path.GetTempPath();
            var infile = tempFolder + "\\" + name;
            var outfile = Path.GetFileNameWithoutExtension(tempFolder + "\\" + name);
            var actualOutFilePattern = $"{tempFolder}\\{outfile}_frame.png";

            /* Downloads the original video file from blob to local storage. */
            log.LogInformation("Dowloading source file from blob to local");
            Stage st = new Stage() { StageName = RunStages.DownloadingBlobImage, StageStatus = ResultStatus.Started };

            try
            {


                using (FileStream fs = new FileStream(infile, FileMode.Create))
                {
                    myBlob.CopyTo(fs);
                }
                st.StatusMessage = $"Downloaded input file from blob - Config - {outPath}   {apiUrl}   {key} ";
                st.StageStatus = ResultStatus.Completed;
            }
            catch (Exception ex)
            {
                st.StatusMessage = $"There was a problem downloading input file from blob. {ex.Message}";
                st.StageStatus = ResultStatus.Exception;
            }

            if (st.StageStatus == ResultStatus.Completed)
            {
                RunResult.StageResults.Add(st);
                st = new Stage() { StageName = RunStages.RunningFFMPEG, StageStatus = ResultStatus.Started };

                var file = System.IO.Path.Combine(folder, "ffmpeg.exe");

                System.Diagnostics.Process process = new System.Diagnostics.Process();
                process.StartInfo.FileName = file;

                process.StartInfo.Arguments = (" -v 1 -i {input} {output} -y")
                    .Replace("{input}", "\"" + infile + "\"")
                    .Replace("{output}", "\"" + actualOutFilePattern + "\"")
                    .Replace("'", "\"");

                log.LogInformation(process.StartInfo.Arguments);

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.OutputDataReceived += new DataReceivedEventHandler(
                    (s, e) =>
                    {
                        log.LogInformation("O: " + e.Data);
                    }
                );
                process.ErrorDataReceived += new DataReceivedEventHandler(
                    (s, e) =>
                    {
                        log.LogInformation("E: " + e.Data);
                    }
                );
                try
                {
                    //start process
                    process.Start();
                    log.LogInformation("process started");
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    process.WaitForExit(5000);
                    process.Close();
                    st.StatusMessage = "Video encoding completed and frames extracted";
                    st.StageStatus = ResultStatus.Completed;
                }
                catch (Exception ex)
                {
                    st.StatusMessage = $"Video encoding failed with the exception {ex.Message}";
                    st.StageStatus = ResultStatus.Exception;
                }

                if (st.StageStatus == ResultStatus.Completed)
                {
                    RunResult.StageResults.Add(st);
                    st = new Stage() { StageName = RunStages.RunningCustomVisionPrediction, StageStatus = ResultStatus.Started };
                    try
                    {
                        var predictionJson = MakePredictionRequest(actualOutFilePattern, apiUrl, key);
                        //Prediction... 
                        double threshold = 37.0d / 100.0d;
                        CustomVisionResult rst = JsonConvert.DeserializeObject<CustomVisionResult>(predictionJson);

                        List<Prediction> predictedPersons = rst.predictions.FindAll(e => e.probability >= threshold && string.Compare(e.tagName, "person", true) == 0);
                        List<Prediction> predictedEmptySeats = rst.predictions.FindAll(e => e.probability >= threshold && string.Compare(e.tagName, "seat", true) == 0);

                        st.StatusMessage = $"Number of Persons identified in the video    :{ predictedPersons.Count} ";
                        st.StageStatus = ResultStatus.Completed;
                        result.PredictionResult = new PredictionStatus();
                        result.PredictionResult.NumberOfPersons = predictedPersons.Count;
                        result.PredictionResult.NumberOfSeats = predictedEmptySeats.Count;
                    }
                    catch (Exception ex)
                    {
                        st.StatusMessage = $"Custom vision API failed with the exception {ex.Message}";
                        st.StageStatus = ResultStatus.Exception;
                    }
                }
            }
            if (st.StageStatus == ResultStatus.Completed)
            {
                RunResult.StageResults.Add(st);
                st = new Stage() { StageName = RunStages.DeleteingTempFiles, StageStatus = ResultStatus.Started };
                try
                {
                    File.Delete(actualOutFilePattern);
                    File.Delete(infile);
                    st.StageStatus = ResultStatus.Completed;
                    st.StatusMessage = "All Temp files are deleted";
                }
                catch (Exception ex)
                {
                    st.StatusMessage = $"Temp file deletion failure {ex.Message}";
                    st.StageStatus = ResultStatus.Exception;
                }
            }
            if (st.StageStatus == ResultStatus.Completed)
            {

                RunResult.OverallStatus = ResultStatus.Completed;
            }
            else
            {
                RunResult.OverallStatus = ResultStatus.Exception;
            }
            RunResult.StageResults.Add(st);

            string outBlobConnection = outPath;
            var storageAccount = CloudStorageAccount.Parse(outBlobConnection);

            var client = storageAccount.CreateCloudBlobClient();
            var container = client.GetContainerReference("outbound");

            var tempJsonResult = $"{tempFolder}\\{outfile}_Result.json";
            var tempJsonLog = $"{tempFolder}\\{outfile}_ProcessLog.json";

            var jsonResultFileName = Path.GetFileName(tempJsonResult);
            var jsonLogFileName = Path.GetFileName(tempJsonLog);


            try
            {
                var resultJson = JsonConvert.SerializeObject(result);
                var logJson = JsonConvert.SerializeObject(RunResult);
                File.WriteAllText(tempJsonResult, resultJson);
                File.WriteAllText(tempJsonLog, logJson);
                var blob = container.GetBlockBlobReference(jsonResultFileName);

                blob.UploadFromFileAsync(tempJsonResult).Wait();
                var blob2 = container.GetBlockBlobReference(jsonLogFileName);
                blob2.UploadFromFileAsync(tempJsonLog).Wait();
                log.LogInformation("Uploaded encoded file to blob");
                File.Delete(tempJsonResult);
                File.Delete(tempJsonLog);
            }
            catch (Exception ex)
            {
                log.LogInformation("There was a problem uploading converted file to blob. " + ex.ToString());

            }



        }
        private static string CreateBase64FromIamge(string path)
        {
            string base64String;
            using (var image = SixLabors.ImageSharp.Image.Load(path))
            {
                using (var ms = new MemoryStream())
                {
                    image.Save(ms, new PngEncoder());
                    image.Dispose();
                    byte[] imageBytes = ms.ToArray();
                    base64String = Convert.ToBase64String(imageBytes);
                    imageBytes = null;
                    ms.Dispose();
                }
            }
            GC.Collect();
            return base64String;
        }


        public static string MakePredictionRequest(string path, string apiUrl, string apiKey)
        {
            var client = new HttpClient();

            // Request headers - replace this example key with your valid Prediction-Key.
            client.DefaultRequestHeaders.Add("Prediction-Key", apiKey);

             
            HttpResponseMessage response;

            // Request body. Try this sample with a locally stored image.
            var base64Img = CreateBase64FromIamge(path);
            byte[] img = Convert.FromBase64String(base64Img);
            using (var content = new ByteArrayContent(img))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                response = client.PostAsync(apiUrl, content).GetAwaiter().GetResult();
                var result = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                return result;
            }

        }
        public static string GetEnvironmentVariable(string name)
        {
            return System.Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);
        }
    }
}

