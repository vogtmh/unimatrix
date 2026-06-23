using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;

namespace UniMatrix
{
    public sealed partial class MainPage
    {
        // ---- Settings panel ----

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsUserId.Text = _settings.UserId ?? "";
            SettingsDeviceId.Text = "Device: " + (_settings.DeviceId ?? "—");
            SettingsHomeserver.Text = "Homeserver: " + _settings.Homeserver;

            HistoryDaysSlider.Value = _settings.HistoryDays;
            HistoryUnlimitedCheck.IsChecked = _settings.HistoryUnlimited;
            UpdateHistoryControls(HistoryDaysSlider, HistoryUnlimitedCheck, HistoryDaysValue);

            UseAccentToggle.IsOn = _settings.UseSystemAccent;

            ShowView(View.Settings);
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e) => ShowView(View.RoomList);

        private void HistoryDaysSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings == null) return;
            int value = (int)e.NewValue;
            _settings.HistoryDays = value;
            UpdateHistoryControls(HistoryDaysSlider, HistoryUnlimitedCheck, HistoryDaysValue);
        }

        private void HistoryUnlimitedCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.HistoryUnlimited = HistoryUnlimitedCheck.IsChecked == true;
            UpdateHistoryControls(HistoryDaysSlider, HistoryUnlimitedCheck, HistoryDaysValue);
        }

        /// <summary>Shared display logic for a history slider + unlimited checkbox + label.</summary>
        private static void UpdateHistoryControls(Slider slider, CheckBox unlimited, TextBlock label)
        {
            if (slider == null || unlimited == null || label == null) return;
            bool isUnlimited = unlimited.IsChecked == true;
            slider.IsEnabled = !isUnlimited;
            label.Text = isUnlimited ? "Unlimited" : (int)slider.Value + " days";
        }

        // ---- Initial setup (shown once after a fresh login) ----

        private void ShowSetup()
        {
            SetupHistorySlider.Value = _settings.HistoryDays;
            SetupUnlimitedCheck.IsChecked = _settings.HistoryUnlimited;
            UpdateHistoryControls(SetupHistorySlider, SetupUnlimitedCheck, SetupHistoryValue);
            ShowView(View.Setup);
        }

        private void SetupHistorySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings == null) return;
            _settings.HistoryDays = (int)e.NewValue;
            UpdateHistoryControls(SetupHistorySlider, SetupUnlimitedCheck, SetupHistoryValue);
        }

        private void SetupUnlimitedCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.HistoryUnlimited = SetupUnlimitedCheck.IsChecked == true;
            UpdateHistoryControls(SetupHistorySlider, SetupUnlimitedCheck, SetupHistoryValue);
        }

        private void SetupContinueButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.SetupComplete = true;
            ShowView(View.RoomList);
            LoadRoomsFromCache();
            StartSync();
        }

        private void UseAccentToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.UseSystemAccent = UseAccentToggle.IsOn;
            Services.ThemeService.Apply(UseAccentToggle.IsOn);
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new Windows.UI.Popups.MessageDialog(
                "Sign out and clear cached chats from this device?", "Sign out");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Sign out"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;

            var choice = await confirm.ShowAsync();
            if (choice.Label != "Sign out") return;

            StopSync();
            try { await _client.LogoutAsync(); } catch { }

            _settings.ClearAccessToken();
            _settings.UserId = null;
            _settings.DeviceId = null;
            _settings.SetupComplete = false;

            _db.ClearAll();
            Rooms.Clear();
            Messages.Clear();
            _currentRoomId = null;

            LoginUserBox.Text = "";
            LoginPassBox.Password = "";
            LoginServerBox.Text = _settings.Homeserver;
            ShowView(View.Login);
        }

        // ---- Create room ----

        private void NewRoomButton_Click(object sender, RoutedEventArgs e)
        {
            NewRoomName.Text = "";
            NewRoomPublic.IsOn = false;
            CreateRoomError.Visibility = Visibility.Collapsed;
            CreateRoomPanel.Visibility = Visibility.Visible;
        }

        private void CreateRoomCancel_Click(object sender, RoutedEventArgs e)
        {
            CreateRoomPanel.Visibility = Visibility.Collapsed;
        }

        private async void CreateRoomConfirm_Click(object sender, RoutedEventArgs e)
        {
            string name = NewRoomName.Text?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                CreateRoomError.Text = "Please enter a room name.";
                CreateRoomError.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                await _client.CreateRoomAsync(name, NewRoomPublic.IsOn);
                CreateRoomPanel.Visibility = Visibility.Collapsed;
                // The new room appears on the next /sync pass.
            }
            catch (Exception ex)
            {
                CreateRoomError.Text = "Could not create room: " + ex.Message;
                CreateRoomError.Visibility = Visibility.Visible;
            }
        }
    }
}
