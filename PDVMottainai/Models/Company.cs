using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class Company
    {
        public int CompanyId { get; set; }
        public int PlanId { get; set; }
        public string OfficialName { get; set; } = string.Empty;
        public string? TradeName { get; set; }
        public string Cnpj { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public decimal? Latitude { get; set; }
        public decimal? Longitude { get; set; }
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
    }
}
