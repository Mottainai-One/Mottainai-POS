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
        public salePage(string loja, string username)
        {
            InitializeComponent(); // Carrega os elementos visuais da página

            string storeSelected = loja; // Guarda o valor na variável
            string usernameUser = username;



            int numSale = 0;
            PopularSaleInformations(numSale, storeSelected, username);

            // 3. Exemplo: Jogando o valor recebido em um TextBlock da salepage chamado "txtTitulo"
            // txtTitulo.Text = "Loja escolhida: " + _lojaSelecionada;
        }

        public void PopularSaleInformations(int numSale, string storeSelected, string usernameUSer)
        {
            // Número da venda do turno
            numSale += 1;
            txtnumSale.Text = numSale.ToString();
            // Data e hora
            ChangeDateTime();
            // Loja
            txtStore.Text = storeSelected;
            // Vendedor

            var listEmployees = MockApiService.Employees;

            var employeeFound = listEmployees.FirstOrDefault(e => e.Email == usernameUSer);

            if (employeeFound != null)
            {
                txtSeller.Text = employeeFound.Name;
            }

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
