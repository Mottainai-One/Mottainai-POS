using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class Inventory
    {
        public int InventoryId { get; set; }
        public int StoreId { get; set; }
        public int BatchId { get; set; }
        public string InventoryType { get; set; } = "NORMAL";
        public decimal CurrentQuantity { get; set; }
        public decimal MinimumQuantity { get; set; }
        public decimal? MaximumQuantity { get; set; }
        public string? Location { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
        public int Version { get; set; } = 1;
    }
}
