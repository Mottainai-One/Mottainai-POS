using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class SaleItem
    {
        public int SaleItemId { get; set; }
        public int SaleId { get; set; }
        public int ProductId { get; set; }
        public int? BatchId { get; set; }
        public decimal QuantitySold { get; set; }
        public decimal UnitPrice { get; set; }
        public string Status { get; set; } = "SOLD";
        public DateTime? CanceledAt { get; set; }
        public decimal Subtotal { get; set; }
        public DateTime SaleDate { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
