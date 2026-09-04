using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class Employee
    {
        public int EmployeeId { get; set; }
        public int StoreId { get; set; }
        public int RoleId { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Cpf { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public bool Active { get; set; } = true;
        public DateTime HireDate { get; set; } = DateTime.Today;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
    }
}
