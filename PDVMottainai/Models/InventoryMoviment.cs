using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class InventoryMoviment
    {
        public int MovementId { get; set; }
        public int InventoryId { get; set; }
        public int? EmployeeId { get; set; }
        public DateTime MovementDate { get; set; } = DateTime.Now;
        public string MovementType { get; set; } = string.Empty;
        public decimal MovedQuantity { get; set; }
        public decimal PreviousBalance { get; set; }
        public decimal CurrentBalance { get; set; }
        public string? Observation { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public int? StoreId { get; set; }
    }
}
