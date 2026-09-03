using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class PosCashMovement
    {
        public int CashMovementId { get; set; }
        public int ShiftId { get; set; }
        public int EmployeeId { get; set; }
        public string MovementType { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public DateTime MovementDate { get; set; } = DateTime.Now;
        public string Reason { get; set; } = string.Empty;
        public string? Observation { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
