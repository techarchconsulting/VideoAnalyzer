
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Text;
 

namespace VideoEncoder
{
    public class ProcessLog
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public ResultStatus OverallStatus { get; set; }


        public  List<Stage> StageResults { get; set; }
    }

    public class Stage
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public ResultStatus StageStatus { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public RunStages StageName { get; set; }

        public string StatusMessage { get; set; }

       
        
    }
    public class ResultMetaData
    {
        public string FileName { get; set; }

        public PredictionStatus PredictionResult { get; set; }


    }


    public class PredictionStatus
    {
        public Int32 NumberOfPersons { get; set; }

        public Int32 NumberOfSeats { get; set; }
    }

    public enum RunStages
    {
        DownloadingBlobImage, RunningFFMPEG, RunningCustomVisionPrediction, UploadingStatusJSON, DeleteingTempFiles
    }
    public enum ResultStatus
    {
        Completed, Exception, Started
    }

    public class BoundingBox
    {
        public double left { get; set; }
        public double top { get; set; }
        public double width { get; set; }
        public double height { get; set; }

    }

    public class Prediction
    {
        public double probability { get; set; }
        public string tagId { get; set; }
        public string tagName { get; set; }
        public BoundingBox boundingBox { get; set; }

    }
    public class CustomVisionResult
    {
        public string id { get; set; }
        public string project { get; set; }
        public string iteration { get; set; }
        public DateTime created { get; set; }
        public List<Prediction> predictions { get; set; }

    }

}
