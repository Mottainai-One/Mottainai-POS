using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class Costumer
    {
        public int CustomerId { get; set; }
        public string FullName { get; set; } = string.Empty;
        public string? Cpf { get; set; }
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public int? AddressId { get; set; }
        public string? ExternalAuthUid { get; set; }
        public DateTime? BirthDate { get; set; }
        public bool Active { get; set; } = true;
        public bool MarketingConsent { get; set; } = false;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
    }
}
