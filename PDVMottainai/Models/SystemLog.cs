using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class SystemLog
    {
        public long LogId { get; set; }
        public string LogLevel { get; set; } = string.Empty;
        public string? Module { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? StackTrace { get; set; }
        public int? UserId { get; set; }
        public string? IpAddress { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
