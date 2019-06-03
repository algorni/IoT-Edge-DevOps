﻿using System;

namespace CustomVisionHelper
{
    public class CustomVisionTrainingDetails
    {
        public string ApiKey { get; set; }
        public string EndPoint { get; set; }
        public Guid ProjectId { get; set; }

        public bool IsValid()
        {
            if (!string.IsNullOrEmpty(ApiKey) && !string.IsNullOrEmpty(EndPoint) && ProjectId != Guid.Empty)
                return true;
            else
                return false;
        }
    }
}
