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
                // Filtra considerando o cenário onde o funcionário pode ter StoreId igual ou uma lista
                var lojasFiltradas = listStores.Where(s => s.StoreId == employeeFound.StoreId);

                Stores.Clear();
                foreach (var lojaItem in lojasFiltradas)
                {
                    Stores.Add(lojaItem);
                }

                // Atribui a fonte de dados e o caminho de exibição
                store.ItemsSource = Stores;
                store.DisplayMemberPath = "Name";

                // Se houver itens, já deixa o primeiro selecionado para testar
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
                imgEye.Source = new BitmapImage(new Uri("pack://application:,,,/IMG/eyeOpen.png"));
                txtPasswordOpen.Text = txtPasswordClose.Password;
                txtPasswordClose.Visibility = Visibility.Collapsed;
                txtPasswordOpen.Visibility = Visibility.Visible;
            }
            else
            {
                imgEye.Source = new BitmapImage(new Uri("pack://application:,,,/IMG/eyeClose.png"));

                txtPasswordClose.Password = txtPasswordOpen.Text;
                txtPasswordOpen.Visibility = Visibility.Collapsed;
                txtPasswordClose.Visibility = Visibility.Visible;
            }
        }

        // Sempre que o usuário mudar algo em suas credenciais:
        private void txtUserName_TextChanged(object sender, TextChangedEventArgs e){FazerLogin();}
        private void txtPasswordClose_PasswordChanged(object sender, RoutedEventArgs e){FazerLogin();}

        // Fazer login
        private void FazerLogin()
        {
            var listUsers = MockApiService.Users;

            var UserFound = listUsers.FirstOrDefault(e => e.Email == txtUserName.Text && (e.PasswordHash == txtPasswordOpen.Text || e.PasswordHash == txtPasswordClose.Password));

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
    }


}
