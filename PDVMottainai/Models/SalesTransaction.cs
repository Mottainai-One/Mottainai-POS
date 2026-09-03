using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class SalesTransaction
    {
        public int SaleId { get; set; }
        public int StoreId { get; set; }
        public int EmployeeId { get; set; }
        public int ShiftId { get; set; }
        public int? CustomerId { get; set; }
        public string? CustomerDocument { get; set; }
        public DateTime SaleDate { get; set; } = DateTime.Now;
        public decimal TotalAmount { get; set; }
        public string Status { get; set; } = "COMPLETED";
        public string? Observation { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
        public int Version { get; set; } = 1;
    }
}
