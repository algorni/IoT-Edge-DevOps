using System;
using System.Collections.Generic;
using System.Text;

namespace CustomVisionHelper
{
    public class CustomVisionIterationDetails : CustomVisionTrainingDetails
    {
        public string IterationName { get; set; }

        public bool IsValidIteration()
        {
            return base.IsValid() && !string.IsNullOrEmpty(IterationName);
        }
    }
}
