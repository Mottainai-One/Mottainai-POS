using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class FiscalDocument
    {
        public int FiscalDocumentId { get; set; }
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public string DocumentType { get; set; } = string.Empty;
        public string? Series { get; set; }
        public string? DocumentNumber { get; set; }
        public string? AccessKey { get; set; }
        public string Status { get; set; } = "AUTHORIZED";
        public DateTime? IssuedAt { get; set; }
        public decimal TotalAmount { get; set; }
        public string? ProtocolNumber { get; set; }
        public string? XmlContent { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
