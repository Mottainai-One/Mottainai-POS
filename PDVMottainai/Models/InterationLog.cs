using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class InterationLog
    {
        public long IntegrationId { get; set; }
        public string IntegrationType { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public string? Payload { get; set; }
        public string? Response { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
