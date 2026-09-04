using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class JobLog
    {
        public long JobId { get; set; }
        public string JobName { get; set; } = string.Empty;
        public string? JobType { get; set; }
        public DateTime StartTime { get; set; } = DateTime.Now;
        public DateTime? EndTime { get; set; }
        public int? DurationSeconds { get; set; }
        public int? RecordsProcessed { get; set; }
        public bool? Success { get; set; }
        public string? Details { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
