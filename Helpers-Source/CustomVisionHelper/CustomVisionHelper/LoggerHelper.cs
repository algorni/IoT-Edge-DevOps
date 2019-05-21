using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text;

namespace CustomVisionHelper
{
    public static class LoggerHelper
    {
        public static void LogTrace(ILogger logger, string message)
        {
            if (logger != null)
            {
                logger.LogTrace(message);
            }
        }

        public static void LogError(ILogger logger, string message)
        {
            if (logger != null)
            {
                logger.LogError(message);
            }
        }
    }
}
