using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class AuditLog
    {
        public long AuditId { get; set; }
        public string TableAffected { get; set; } = string.Empty;
        public string Operation { get; set; } = string.Empty;
        public string RecordId { get; set; } = string.Empty;
        public int? UserId { get; set; }
        public string? OldData { get; set; }
        public string? NewData { get; set; }
        public DateTime OperationDate { get; set; } = DateTime.Now;
    }
}
