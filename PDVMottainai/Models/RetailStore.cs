using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class RetailStore
    {
        public int StoreId { get; set; }
        public int CompanyId { get; set; }
        public int AddressId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Cnpj { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
    }
}
