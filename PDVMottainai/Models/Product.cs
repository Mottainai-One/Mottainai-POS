using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class Product
    {
        public int ProductId { get; set; }
        public int CategoryId { get; set; }
        public int TaxProfileId { get; set; }
        public string Sku { get; set; } = string.Empty;
        public string Barcode { get; set; } = string.Empty;
        public string Ncm { get; set; } = string.Empty;
        public string? Cest { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string? Brand { get; set; }
        public string UnitMeasure { get; set; } = string.Empty;
        public decimal? Weight { get; set; }
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
        public int Version { get; set; } = 1;
    

    }
}
