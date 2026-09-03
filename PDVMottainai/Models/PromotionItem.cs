using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class PromotionItem
    {
        public int PromotionItemId { get; set; }
        public int PromotionId { get; set; }
        public int ProductId { get; set; }
        public decimal OriginalPrice { get; set; }
        public decimal PromotionalPrice { get; set; }
        public decimal DiscountPercent { get; set; }
        public decimal? QuantityAvailable { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
