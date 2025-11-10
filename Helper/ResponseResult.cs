using Pulsar_DomeDriver.Config;
using Pulsar_DomeDriver.Driver;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Pulsar_DomeDriver.Helper
{
    public class ResponseResult
    {
        public string? Response { get; set; }
        public bool IsMatch { get; set; }
        public int RetriesUsed { get; set; }
        public string Command { get; set; } = string.Empty;
    }
}
