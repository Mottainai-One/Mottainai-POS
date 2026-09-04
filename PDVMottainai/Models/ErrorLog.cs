using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class ErrorLog
    {
        public long ErrorId { get; set; }
        public string? ErrorCode { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string? FunctionName { get; set; }
        public string? Parameters { get; set; }
        public string? StackTrace { get; set; }
        public int? UserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
