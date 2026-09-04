using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class SalePayment
    {
        public int SalePaymentId { get; set; }
        public int SaleId { get; set; }
        public DateTime SaleDate { get; set; }
        public string PaymentMethod { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public int Installments { get; set; } = 1;
        public string? AuthorizationCode { get; set; }
        public string? Nsu { get; set; }
        public string? TransactionId { get; set; }
        public DateTime PaidAt { get; set; } = DateTime.Now;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
