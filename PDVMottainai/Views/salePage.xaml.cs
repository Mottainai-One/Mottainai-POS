using PDVMottainai.Services;
using System;
using System.Collections.Generic;
using System.Text;
using System.Timers;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace PDVMottainai.Views
{
    /// <summary>
    /// Lógica interna para salePage.xaml
    /// </summary>
    public partial class salePage : Page
    {
        private DispatcherTimer? _timer;

        public salePage()
        {
            
            InitializeComponent();
            
        }
        public salePage(string store, string emailUser)
        {
            InitializeComponent(); 


            int numSale = 0;
            TrendingSaleInformations(numSale, store, emailUser);
        }

        public void TrendingSaleInformations(int numSale, string storeSelected, string emailUser)
        {
            // Número da venda do turno
            numSale += 1;
            //txtnumSale.Text = numSale.ToString();

            // Data e hora
            ChangeDateTime();

            // Loja
            txtStore.Text = "Loja: " + storeSelected;

            // E-mail vendedor
            //var listEmployees = MockApiService.Employees;

            //var employeeFound = listEmployees.FirstOrDefault(e => e.Email == emailUser);

            //if (employeeFound != null)
            //{
            //    txtSeller.Text = employeeFound.Name;
            //}

        }
        public void ChangeDateTime()
        {
            // Inicializa o temporizador
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1); // Define o intervalo de 1 segundo

            // Associa o evento que roda a cada tique do relógio
            _timer.Tick += (sender, e) =>
            {
                txtDateTime.Text = $"{DateTime.Now:dd/MM/yyyy} | {DateTime.Now:HH:mm:ss}";
            };

            // Dispara
            _timer.Start();
        }

    }

    }
