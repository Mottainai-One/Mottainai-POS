using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Models
{
    internal class AppUser
    {
        public int UserId { get; set; }
        public int EmployeeId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string PasswordHash { get; set; } = string.Empty;
        public DateTime? LastLogin { get; set; }
        public bool Active { get; set; } = true;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public DateTime? DeletedAt { get; set; }
    }
}
