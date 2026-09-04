using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    public class PosTerminal
    {
        public int TerminalId { get; set; }
        public int StoreId { get; set; }
        public string TerminalCode { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Hostname { get; set; }
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }
}
