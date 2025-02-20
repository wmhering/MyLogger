using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace MyLogger.Common
{
    internal interface ICreateLogEntries
    {
        void CreateLogEntry(LogLevel logLevel, string category, string message, Exception? exception);
    }
}
