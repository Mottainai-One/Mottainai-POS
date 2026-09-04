using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class PosCancelRequest
    {
        public int CancelRequestId { get; set; }
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public int? SaleItemId { get; set; }
        public int RequestedBy { get; set; }
        public int? ApprovedBy { get; set; }
        public string TargetType { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = "PENDING";
        public DateTime RequestedAt { get; set; } = DateTime.Now;
        public DateTime? DecidedAt { get; set; }
        public DateTime? ExecutedAt { get; set; }
        public string? DecisionObservation { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
