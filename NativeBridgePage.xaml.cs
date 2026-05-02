using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SteamRipApp.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace SteamRipApp
{
    public sealed partial class NativeBridgePage : Page
    {
        private ObservableCollection<string> _visibleLogs = new ObservableCollection<string>();
        private List<(string Message, string Category)> _allLogs = new List<(string, string)>();

        public NativeBridgePage()
        {
            this.InitializeComponent();
            LogList.ItemsSource = _visibleLogs;

            if (GlobalSettings.IsSteamIntegrationEnabled)
            {
                Logger.Log("[Settings] Steam Integration enabled.");
            }
            else
            {
                Logger.Log("[Settings] Steam Integration disabled.");
            }

            Logger.Log("[NativeBridge] Page initialized in legacy mode.");
        }

        private void ProcessIncomingLog(string msg, string category)
        {
            DispatcherQueue.TryEnqueue(() => {
                _allLogs.Add((msg, category));
                if (_allLogs.Count > 1000) _allLogs.RemoveAt(0);

                _visibleLogs.Add(msg);
                if (_visibleLogs.Count > 500) _visibleLogs.RemoveAt(0);
            });
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) { }
        private void UserSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) { }
        private void ServiceToggle_Toggled(object sender, RoutedEventArgs e) { }
        private void KillService_Click(object sender, RoutedEventArgs e) { }
    }
}