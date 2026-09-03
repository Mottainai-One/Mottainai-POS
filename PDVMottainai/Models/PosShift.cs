using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class PosShift
    {
        public int ShiftId { get; set; }
        public int TerminalId { get; set; }
        public int EmployeeId { get; set; }
        public DateTime OpenedAt { get; set; } = DateTime.Now;
        public decimal OpeningAmount { get; set; }
        public DateTime? ClosedAt { get; set; }
        public decimal? ClosingAmount { get; set; }
        public decimal? ExpectedAmount { get; set; }
        public string Status { get; set; } = "OPEN";
        public string? Observation { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
