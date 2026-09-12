using PDVMottainai.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace PDVMottainai.Services
{
    internal class MockApiService
    {
        // Lista estática que vai armazenar os dados na memória
        public static List<AppUser> Users { get; set; } = new List<AppUser>
    {
        new AppUser
        {
            UserId = 1,
            EmployeeId = 101,
            Email = "1",
            PasswordHash = "1", // Num cenário real, isso estaria criptografado
            LastLogin = DateTime.Now.AddDays(-1),
            Active = true,
            CreatedAt = DateTime.Now.AddMonths(-6),
            UpdatedAt = DateTime.Now.AddDays(-2)
        },
        new AppUser
        {
            UserId = 2,
            EmployeeId = 102,
            Email = "maria.santos@empresa.com",
            PasswordHash = "hash_falso_456",
            LastLogin = DateTime.Now.AddHours(-5),
            Active = true,
            CreatedAt = DateTime.Now.AddMonths(-3),
            UpdatedAt = DateTime.Now.AddHours(-5)
        },
        new AppUser
        {
            UserId = 3,
            EmployeeId = 103,
            Email = "pedro.inativo@empresa.com",
            PasswordHash = "hash_falso_789",
            LastLogin = null,
            Active = false,
            CreatedAt = DateTime.Now.AddYears(-1),
            UpdatedAt = DateTime.Now.AddDays(-15),
            DeletedAt = DateTime.Now.AddDays(-15)
        }
    };

        public static List<PosTerminal> Terminals { get; set; } = new List<PosTerminal>
        {        
            new PosTerminal
            {
                TerminalId = 1,
                StoreId = 1,
                TerminalCode = "TERM-001",
                Name = "Caixa Principal 01eeeeeeeeee",
                Hostname = "POS-STORE101-01",
                Active = true,
                CreatedAt = new DateTime(2026, 01, 15, 10, 0, 0),
                UpdatedAt = new DateTime(2026, 01, 15, 10, 0, 0)
            },
            new PosTerminal
            {
                TerminalId = 2,
                StoreId = 1,
                TerminalCode = "TERM-002",
                Name = "Caixa Rápido 02",
                Hostname = "POS-STORE101-02",
                Active = true,
                CreatedAt = new DateTime(2026, 01, 15, 11, 30, 0),
                UpdatedAt = new DateTime(2026, 02, 10, 14, 20, 0)
            },
            new PosTerminal
            {
                TerminalId = 3,
                StoreId = 102,
                TerminalCode = "TERM-003",
                Name = "Autoatendimento 01",
                Hostname = null,
                Active = false,
                CreatedAt = new DateTime(2026, 02, 01, 9, 15, 0),
                UpdatedAt = new DateTime(2026, 03, 01, 8, 0, 0)
            }
        };
        
        public static List<Employee> Employees { get; set; } = new List<Employee>
{
    new Employee
    {
        EmployeeId = 101,
        StoreId = 1,
        RoleId = 1,
        Name = "João Silva",
        Cpf = "12345678901",
        Email = "1",
        Phone = "11987654321",
        Active = true,
        HireDate = DateTime.Today.AddMonths(-6),
        CreatedAt = DateTime.Now.AddMonths(-6),
        UpdatedAt = DateTime.Now.AddDays(-2),
        DeletedAt = null
    },
    new Employee
    {
        EmployeeId = 102,
        StoreId = 1,
        RoleId = 2,
        Name = "Maria Santos",
        Cpf = "98765432109",
        Email = "maria.santos@empresa.com",
        Phone = "11912345678",
        Active = true,
        HireDate = DateTime.Today.AddMonths(-3),
        CreatedAt = DateTime.Now.AddMonths(-3),
        UpdatedAt = DateTime.Now.AddHours(-5),
        DeletedAt = null
    },
    new Employee
    {
        EmployeeId = 103,
        StoreId = 2,
        RoleId = 3,
        Name = "Pedro Inativo",
        Cpf = "45678912301",
        Email = "pedro.inativo@empresa.com",
        Phone = null,
        Active = false,
        HireDate = DateTime.Today.AddYears(-1),
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddDays(-15),
        DeletedAt = DateTime.Now.AddDays(-15)
    }
};

        public static List<RetailStore> Stores { get; set; } = new List<RetailStore>
{
    new RetailStore
    {
        StoreId = 1,
        CompanyId = 10,
        AddressId = 100,
        Name = "Matriz Paulista",
        Cnpj = "12345678000199",
        Email = "matriz@empresa.com",
        Phone = "1133334444",
        Latitude = -23.550520m,
        Longitude = -46.633308m,
        Active = true,
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddDays(-10),
        DeletedAt = null
    },
        new RetailStore
    {
        StoreId = 1,
        CompanyId = 10,
        AddressId = 100,
        Name = "Matriz Paugfdsdfglista",
        Cnpj = "12345678000199",
        Email = "matriz@empresa.com",
        Phone = "1133334444",
        Latitude = -23.550520m,
        Longitude = -46.633308m,
        Active = true,
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddDays(-10),
        DeletedAt = null
    },
    new RetailStore
    {
        StoreId = 2,
        CompanyId = 10,
        AddressId = 101,
        Name = "Filial Pinheiros",
        Cnpj = "12345678000280",
        Email = "pinheiros@empresa.com",
        Phone = "1133335555",
        Latitude = -23.561414m,
        Longitude = -46.702117m,
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-6),
        UpdatedAt = DateTime.Now.AddDays(-5),
        DeletedAt = null
    },
    new RetailStore
    {
        StoreId = 3,
        CompanyId = 10,
        AddressId = 102,
        Name = "Filial Fechada Mooca",
        Cnpj = "12345678000361",
        Email = "mooca@empresa.com",
        Phone = null,
        Latitude = null,
        Longitude = null,
        Active = false,
        CreatedAt = DateTime.Now.AddYears(-2),
        UpdatedAt = DateTime.Now.AddMonths(-1),
        DeletedAt = DateTime.Now.AddMonths(-1)
    }
};

        public static List<Company> Companies { get; set; } = new List<Company>
{
    new Company
    {
        CompanyId = 10,
        PlanId = 2,
        OfficialName = "Mottainai Soluções em Tecnologia S.A.",
        TradeName = "Mottainai Tech",
        Cnpj = "12345678000199",
        Email = "contato@mottainai.com",
        Phone = "1140028922",
        Latitude = -23.550520m,
        Longitude = -46.633308m,
        Active = true,
        CreatedAt = DateTime.Now.AddYears(-2),
        UpdatedAt = DateTime.Now.AddMonths(-1),
        DeletedAt = null
    },
    new Company
    {
        CompanyId = 11,
        PlanId = 1,
        OfficialName = "Comércio de Alimentos São Judas Tadeu Ltda",
        TradeName = "Mercado São Judas",
        Cnpj = "98765432000188",
        Email = "suporte@saojudas.com",
        Phone = "1132221111",
        Latitude = -23.600000m,
        Longitude = -46.650000m,
        Active = true,
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddDays(-10),
        DeletedAt = null
    }
};

        public static List<ProductCategory> Categories { get; set; } = new List<ProductCategory>
{
    new ProductCategory
    {
        CategoryId = 1,
        Name = "Eletrônicos",
        Description = "Dispositivos eletrônicos, smartphones e acessórios",
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-6),
        UpdatedAt = DateTime.Now.AddDays(-15),
        DeletedAt = null
    },
    new ProductCategory
    {
        CategoryId = 2,
        Name = "Vestuário",
        Description = "Roupas masculinas, femininas e infantis",
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-4),
        UpdatedAt = DateTime.Now.AddDays(-5),
        DeletedAt = null
    },
    new ProductCategory
    {
        CategoryId = 3,
        Name = "Alimentos e Bebidas",
        Description = "Produtos de mercearia e perecíveis",
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-1),
        UpdatedAt = DateTime.Now.AddDays(-2),
        DeletedAt = null
    },
    new ProductCategory
    {
        CategoryId = 4,
        Name = "Casa e Decoração",
        Description = "Utensílios domésticos e itens decorativos",
        Active = false,
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddMonths(-3),
        DeletedAt = DateTime.Now.AddMonths(-3)
    }
};

        public static List<Product> Products { get; set; } = new List<Product>
{
    new Product
    {
        ProductId = 1,
        CategoryId = 1,
        TaxProfileId = 1,
        Sku = "ELET-SM-001",
        Barcode = "7891029384756",
        Ncm = "8517.13.00",
        Cest = "21.028.00",
        Name = "Smartphone Quantum X 128GB",
        Description = "Smartphone com tela OLED de 6.4 polegadas, 6GB RAM e câmera dupla",
        Brand = "Quantum",
        UnitMeasure = "UN",
        Weight = 0.185m,
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-5),
        UpdatedAt = DateTime.Now.AddDays(-10),
        DeletedAt = null,
        Version = 1
    },
    new Product
    {
        ProductId = 2,
        CategoryId = 2,
        TaxProfileId = 2,
        Sku = "VEST-CM-042",
        Barcode = "7894561237890",
        Ncm = "6109.10.00",
        Cest = "28.038.00",
        Name = "Camiseta Básica Algodão Pima Preta",
        Description = "Camiseta 100% algodão pima, modelagem slim fit",
        Brand = "UrbanStyle",
        UnitMeasure = "UN",
        Weight = 0.210m,
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-3),
        UpdatedAt = DateTime.Now.AddDays(-2),
        DeletedAt = null,
        Version = 1
    },
    new Product
    {
        ProductId = 3,
        CategoryId = 3,
        TaxProfileId = 3,
        Sku = "ALIM-CA-105",
        Barcode = "7897894561230",
        Ncm = "0901.21.00",
        Cest = "17.012.00",
        Name = "Café Gourmet em Grãos 500g",
        Description = "Café 100% arábica torra média, notas de chocolate e castanha",
        Brand = "Café Serra",
        UnitMeasure = "UN",
        Weight = 0.500m,
        Active = true,
        CreatedAt = DateTime.Now.AddMonths(-1),
        UpdatedAt = DateTime.Now.AddDays(-1),
        DeletedAt = null,
        Version = 2
    },
    new Product
    {
        ProductId = 4,
        CategoryId = 4,
        TaxProfileId = 1,
        Sku = "CASA-LU-088",
        Barcode = "7893216549870",
        Ncm = "9405.20.00",
        Cest = "12.005.00",
        Name = "Luminária de Mesa LED Minimalista",
        Description = "Luminária articulada com controle de intensidade de luz touch",
        Brand = "Lumina",
        UnitMeasure = "UN",
        Weight = 0.950m,
        Active = false,
        CreatedAt = DateTime.Now.AddYears(-1),
        UpdatedAt = DateTime.Now.AddMonths(-4),
        DeletedAt = DateTime.Now.AddMonths(-4),
        Version = 1
    }
};
    }
    
}
