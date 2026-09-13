using PDVMottainai.Models;
using PDVMottainai.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PDVMottainai.Views
{
    /// <summary>
    /// Lógica interna para LoginPage.xaml
    /// </summary>
    public partial class LoginPage : Page
    {
        // Cria os ObservableCollection que serão usados no ComboBox
        public ObservableCollection<RetailStore> Stores { get; set; }
        public ObservableCollection<PosTerminal> Terminals { get; set; }

        public LoginPage()
        {
            InitializeComponent();

            // Inicia os ObservableCollection que serão usados no ComboBox
            Stores = new ObservableCollection<RetailStore>();
            Terminals = new ObservableCollection<PosTerminal>();

        }

        // Quando o usuário escolher uma loja:
        private void loja_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (store.SelectedValue != null)
            {
                int idSelecionado = (int)store.SelectedValue;

                listTerminals(idSelecionado);
            }
        }

        //Lista lojas disponíveis para o funcionário
        private void listStores(int currentUserId)
        {
            var listStores = MockApiService.Stores;
            var listEmployees = MockApiService.Employees;

            var employeeFound = listEmployees.FirstOrDefault(e => e.StoreId == currentUserId);

            if (employeeFound != null)
            {
                // Filtra as lojas do funcionário
                var lojasFiltradas = listStores.Where(s => s.StoreId == employeeFound.StoreId);

                Stores.Clear();
                foreach (var lojaItem in lojasFiltradas)
                {
                    Stores.Add(lojaItem);
                }

                // Atribui a fonte de dados e o caminho de exibição
                store.ItemsSource = Stores;
                store.DisplayMemberPath = "Name";

                // Se houver itens, já deixa o primeiro selecionado
                if (Stores.Count > 0)
                {
                    store.SelectedIndex = 0;
                }

                
            }
        }

        //Lista terminais disponíveis para a loja
        private void listTerminals(int storeId)
        {

            var listTerminals = MockApiService.Terminals;
            var listStores = MockApiService.Stores;

            var storesFound = listStores.FirstOrDefault(s => s.StoreId == storeId);

            if (storesFound != null)
            {
                var terminaisFiltrados = listTerminals.Where(s => s.StoreId == storesFound.StoreId);

                Terminals.Clear();

                foreach (var terminalItem in terminaisFiltrados)
                {
                    Terminals.Add(terminalItem);
                }

                terminal.ItemsSource = Terminals;
                terminal.DisplayMemberPath = "Name";

                if (Stores.Count > 0)
                {
                    terminal.SelectedIndex = 0;
                }

            }


        }

        // Mostrar e ocultar senha
        private void TogglePassword_Click(object sender, RoutedEventArgs e)
        {
            if (txtPasswordClose.Visibility == Visibility.Visible)
            {
                imgEye.Source = new BitmapImage(new Uri("pack://application:,,,/IMG/eyeOpenIcon.png"));
                txtPasswordOpen.Text = txtPasswordClose.Password;
                txtPasswordClose.Visibility = Visibility.Collapsed;
                txtPasswordOpen.Visibility = Visibility.Visible;
            }
            else
            {
                imgEye.Source = new BitmapImage(new Uri("pack://application:,,,/IMG/eyeCloseIcon.png"));

                txtPasswordClose.Password = txtPasswordOpen.Text;
                txtPasswordOpen.Visibility = Visibility.Collapsed;
                txtPasswordClose.Visibility = Visibility.Visible;
            }
        }

        // Sempre que o usuário mudar algo em suas credenciais:
        private void txtEmail_TextChanged(object sender, TextChangedEventArgs e){ TakeStores();}
        private void txtPasswordClose_PasswordChanged(object sender, RoutedEventArgs e){ TakeStores();}

        // Fazer login
        private void TakeStores()
        {
            var listUsers = MockApiService.Users;

            var UserFound = listUsers.FirstOrDefault(e => e.Email == txtEmail.Text && (e.PasswordHash == txtPasswordOpen.Text || e.PasswordHash == txtPasswordClose.Password));

            if (UserFound != null)
            {
                listStores(UserFound.UserId);
                txtMsg.Visibility = Visibility.Collapsed;
            }
            else
            {
                txtMsg.Text = "Credenciais inválidas!";
                txtMsg.Visibility = Visibility.Visible;
                terminal.ItemsSource = null;
                store.ItemsSource = null;
            }
        }

        private void LogIn_Click(object sender, RoutedEventArgs e)
        {
            var listUsers = MockApiService.Users;

            var UserFound = listUsers.FirstOrDefault(e => e.Email == txtEmail.Text && (e.PasswordHash == txtPasswordOpen.Text || e.PasswordHash == txtPasswordClose.Password));

            if (UserFound != null)
            {
                //  Pega diretamente o objeto RetailStore selecionado no ComboBox
                if (store.SelectedItem is PDVMottainai.Models.RetailStore selectedStore)
                {
                    salePage proximaPagina = new salePage(selectedStore.Name, UserFound.Email);

                    this.NavigationService.Navigate(proximaPagina);
                }
                else
                {
                    MessageBox.Show("Por favor, selecione uma loja.");
                }
            }

        }

    }
}
