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
            ServiceToggle.IsOn = GlobalSettings.IsSteamIntegrationEnabled;
            foreach (var log in NativeBridgeService.GetLogs()) 
            {
                string cat = "GENERAL";
                if (log.Contains("[PATCH]")) cat = "PATCH";
                else if (log.Contains("[DISK]")) cat = "DISK";
                else if (log.Contains("[LAUNCHER]")) cat = "LAUNCHER";
                else if (log.Contains("[CONFIG]")) cat = "CONFIG";
                else if (log.Contains("[SYSTEM]")) cat = "SYSTEM";
                else if (log.Contains("[PROCESS]")) cat = "PROCESS";
                else if (log.Contains("[INTERFACE]")) cat = "INTERFACE";
                else if (log.Contains("[GENERAL]")) cat = "GENERAL";
                ProcessIncomingLog(log, cat);
            }
            NativeBridgeService.OnLog += (msg, cat) => ProcessIncomingLog(msg, cat);
            LoadUsers();
            _ = StartWorkerMonitor();
        }
        private async Task StartWorkerMonitor()
        {
            while (true)
            {
                try {
                    using var ws = new System.Net.WebSockets.ClientWebSocket();
                    await ws.ConnectAsync(new Uri("ws://localhost:8081"), default);
                    byte[] buffer = new byte[8192];
                    while (ws.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), default);
                        if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                        {
                            string json = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                            using var doc = System.Text.Json.JsonDocument.Parse(json);
                            if (doc.RootElement.TryGetProperty("action", out var action) && action.GetString() == "log")
                            {
                                string msg = doc.RootElement.GetProperty("message").GetString() ?? "";
                                string cat = doc.RootElement.TryGetProperty("category", out var c) ? c.GetString() ?? "GENERAL" : "GENERAL";
                                if (!NativeBridgeService.IsRunning) ProcessIncomingLog(msg, cat);
                            }
                        }
                    }
                } catch { }
                await Task.Delay(3000);
            }
        }
        private void ProcessIncomingLog(string msg, string category)
        {
            DispatcherQueue.TryEnqueue(() => {
                _allLogs.Add((msg, category));
                if (_allLogs.Count > 1000) _allLogs.RemoveAt(0);
                if (IsCategoryVisible(category))
                {
                    _visibleLogs.Add(msg);
                    if (_visibleLogs.Count > 500) _visibleLogs.RemoveAt(0);
                    if (_visibleLogs.Count > 0) LogList.ScrollIntoView(_visibleLogs[_visibleLogs.Count - 1]);
                }
            });
        }
        private bool IsCategoryVisible(string category)
        {
            return category switch {
                "LAUNCHER" => Filter_LAUNCHER.IsChecked == true,
                "SYSTEM" => Filter_SYSTEM.IsChecked == true,
                "PROCESS" => Filter_PROCESS.IsChecked == true,
                "CONFIG" => Filter_CONFIG.IsChecked == true,
                "DISK" => Filter_DISK.IsChecked == true,
                "PATCH" => Filter_PATCH.IsChecked == true,
                "INTERFACE" => Filter_INTERFACE.IsChecked == true,
                "GENERAL" => Filter_GENERAL.IsChecked == true,
                _ => true
            };
        }
        private void Filter_Changed(object sender, RoutedEventArgs e)
        {
            if (_visibleLogs == null) return;
            _visibleLogs.Clear();
            foreach (var log in _allLogs)
            {
                if (IsCategoryVisible(log.Category))
                {
                    _visibleLogs.Add(log.Message);
                }
            }
            if (_visibleLogs.Count > 0) LogList.ScrollIntoView(_visibleLogs[_visibleLogs.Count - 1]);
        }
        private void LoadUsers()
        {
            var users = SteamManager.GetSteamUsers();
            UserSelector.ItemsSource = users;
            if (!string.IsNullOrEmpty(GlobalSettings.SelectedSteamAccountId))
            {
                var selected = users.Find(u => u.AccountId == GlobalSettings.SelectedSteamAccountId);
                if (selected != null) UserSelector.SelectedItem = selected;
            }
            else if (users.Count > 0)
            {
                var recent = users.Find(u => u.IsMostRecent) ?? users[0];
                UserSelector.SelectedItem = recent;
            }
        }
        private void UserSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserSelector.SelectedItem is SteamUser user)
            {
                GlobalSettings.SelectedSteamAccountId = user.AccountId;
                GlobalSettings.Save();
            }
        }
        private void ServiceToggle_Toggled(object sender, RoutedEventArgs e)
        {
            GlobalSettings.IsSteamIntegrationEnabled = ServiceToggle.IsOn;
            GlobalSettings.Save();
            if (ServiceToggle.IsOn) {
                NativeBridgeService.Start();
            } else {
                NativeBridgeService.Stop();
                Task.Run(() => {
                    try {
                        var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                        var existingWorkers = System.Diagnostics.Process.GetProcessesByName("SteamRipApp")
                            .Where(p => p.Id != currentProcess.Id)
                            .ToList();
                        foreach (var p in existingWorkers) p.Kill();
                    } catch { }
                });
            }
        }
        private void KillService_Click(object sender, RoutedEventArgs e)
        {
            try {
                var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
                var existingWorkers = System.Diagnostics.Process.GetProcessesByName("SteamRipApp")
                    .Where(p => p.Id != currentProcess.Id)
                    .ToList();
                foreach (var p in existingWorkers) p.Kill();
                NativeBridgeService.Log("Background services reset.", "SYSTEM");
            } catch (Exception ex) {
                Logger.LogError("KillService", ex);
            }
        }
    }
}

