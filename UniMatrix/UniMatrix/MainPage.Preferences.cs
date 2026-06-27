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
            UpdateHistoryControls(HistoryDaysSlider, HistoryDaysValue);

            UseAccentToggle.IsOn = _settings.UseSystemAccent;

            NotifyDirectToggle.IsOn = _settings.NotifyDirectMessages;
            NotifyGroupsToggle.IsOn = _settings.NotifyGroupRooms;
            LiveTileModeCombo.SelectedIndex = (int)_settings.LiveTileMode;

            ShowView(View.Settings);

            // Compute on-disk usage off the UI thread; update the label when ready.
            var _ = UpdateStorageUsageAsync();

            // Refresh key-backup status text + button states.
            var __ = RefreshBackupStatusAsync();

            // Refresh the device-verification (cross-signing) status.
            RefreshCrossSigningStatus();
        }

        private async System.Threading.Tasks.Task UpdateStorageUsageAsync()
        {
            try
            {
                long dbBytes = 0, mediaBytes = 0;
                var local = Windows.Storage.ApplicationData.Current.LocalFolder;

                // The SQLite database is unimatrix.db plus its WAL/SHM sidecar files.
                foreach (var name in new[] { "unimatrix.db", "unimatrix.db-wal", "unimatrix.db-shm" })
                {
                    try
                    {
                        var f = await local.GetFileAsync(name);
                        var p = await f.GetBasicPropertiesAsync();
                        dbBytes += (long)p.Size;
                    }
                    catch { /* file may not exist (e.g. no WAL yet) */ }
                }

                // Cached media lives in the "media" subfolder.
                try
                {
                    var media = await local.GetFolderAsync("media");
                    foreach (var f in await media.GetFilesAsync())
                    {
                        var p = await f.GetBasicPropertiesAsync();
                        mediaBytes += (long)p.Size;
                    }
                }
                catch { /* folder may not exist yet */ }

                if (StorageUsage != null)
                    StorageUsage.Text = "Database: " + FormatBytes(dbBytes) +
                                        "  ·  Images: " + FormatBytes(mediaBytes);
            }
            catch
            {
                if (StorageUsage != null) StorageUsage.Text = "Storage usage unavailable";
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        private void SettingsCloseButton_Click(object sender, RoutedEventArgs e) => ShowView(View.RoomList);

        private void HistoryDaysSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings == null) return;
            int value = (int)e.NewValue;
            _settings.HistoryDays = value;
            UpdateHistoryControls(HistoryDaysSlider, HistoryDaysValue);
            // Changing the window is the user's way of "fixing" an over-eager backfill, so
            // clear any prior cancel and let it run for the new window.
            ResumeBackfillAll();
        }

        /// <summary>Shared display logic for a history slider + label.</summary>
        private static void UpdateHistoryControls(Slider slider, TextBlock label)
        {
            if (slider == null || label == null) return;
            label.Text = (int)slider.Value + " days";
        }

        // ---- Initial setup (shown once after a fresh login) ----

        private void ShowSetup()
        {
            SetupHistorySlider.Value = _settings.HistoryDays;
            UpdateHistoryControls(SetupHistorySlider, SetupHistoryValue);
            ShowView(View.Setup);
        }

        private void SetupHistorySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_settings == null) return;
            _settings.HistoryDays = (int)e.NewValue;
            UpdateHistoryControls(SetupHistorySlider, SetupHistoryValue);
        }

        private async void SetupContinueButton_Click(object sender, RoutedEventArgs e)
        {
            _settings.SetupComplete = true;
            ShowView(View.RoomList);
            LoadRoomsFromCache();

            // Initialize encryption for the freshly logged-in account before syncing so the first
            // sync's to-device room keys and encrypted events are handled.
            await EnsureCryptoAsync();
            StartSync();

            // Start the periodic message-notification background task now that we're signed in.
            var _ = Services.NotificationTask.RegisterAsync();
        }

        private void UseAccentToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.UseSystemAccent = UseAccentToggle.IsOn;
            Services.ThemeService.Apply(UseAccentToggle.IsOn);
        }

        private void NotifyDirectToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.NotifyDirectMessages = NotifyDirectToggle.IsOn;
            UpdateNotificationTaskRegistration();
        }

        private void NotifyGroupsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;
            _settings.NotifyGroupRooms = NotifyGroupsToggle.IsOn;
            UpdateNotificationTaskRegistration();
        }

        private void LiveTileModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_settings == null) return;
            int idx = LiveTileModeCombo.SelectedIndex;
            if (idx < 0) return;

            var mode = (Services.LiveTileMode)idx;
            _settings.LiveTileMode = mode;

            // Turning the tile off should clear any message currently pinned to it.
            if (mode == Services.LiveTileMode.Off)
                Services.TileService.Clear();

            UpdateNotificationTaskRegistration();
        }

        /// <summary>Registers the background task when at least one of the message notifications or
        /// the live tile is enabled, and unregisters it when everything is off (no point waking up
        /// to do nothing).</summary>
        private void UpdateNotificationTaskRegistration()
        {
            bool anyEnabled = _settings.NotifyDirectMessages || _settings.NotifyGroupRooms ||
                              _settings.LiveTileMode != Services.LiveTileMode.Off;
            if (anyEnabled)
            {
                var _ = Services.NotificationTask.RegisterAsync();
            }
            else
            {
                Services.NotificationTask.Unregister();
            }
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

            // Stop background notifications for the signed-out account.
            Services.NotificationTask.Unregister();

            _settings.ClearAccessToken();
            _settings.ClearPickleKey();
            _settings.UserId = null;
            _settings.DeviceId = null;
            _settings.SetupComplete = false;

            _db.ClearAll();
            _db.ClearCryptoTables();
            try { _crypto?.DisableCrossSigning(); } catch { }
            try { _ssss?.Lock(); } catch { }
            _crypto = null;
            _backup = null;
            _ssss = null;
            _verify = null;
            _recoveryPromptShown = false;
            Rooms.Clear();
            Messages.Clear();
            _currentRoomId = null;

            LoginUserBox.Text = "";
            LoginPassBox.Password = "";
            LoginServerBox.Text = _settings.Homeserver;
            ShowView(View.Login);
        }

        private async void WipeCacheButton_Click(object sender, RoutedEventArgs e)
        {
            var confirm = new Windows.UI.Popups.MessageDialog(
                "This clears all locally cached rooms and messages and re-downloads everything from the server. You stay signed in. Continue?",
                "Wipe cache & resync");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Wipe & resync"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;

            var choice = await confirm.ShowAsync();
            if (choice.Label != "Wipe & resync") return;

            // Stop all background work before clearing the database so nothing writes
            // to the connection while it's being wiped.
            StopBackfillAll();
            StopSync();

            // ClearAll() drops messages/rooms/members/media and ALL meta, which includes
            // the next_batch sync token and the per-room backfill tokens/done flags, so the
            // next sync starts fresh and the background backfill re-pulls full history.
            _db.ClearAll();
            Rooms.Clear();
            Messages.Clear();
            _currentRoomId = null;

            // Delete the cached media files on disk too (ClearAll only drops the DB rows).
            // Best-effort: files still locked by a live image are skipped, and re-downloads
            // now write unique filenames so they never collide with a leftover locked file.
            await _media.ClearMediaCacheAsync();

            // Clear any earlier cancel so the fresh sync's backfill can run.
            ResumeBackfillAll();

            // Reflect the freed space immediately.
            var _ = UpdateStorageUsageAsync();

            // Back to the room list and kick off a clean full sync.
            ShowView(View.RoomList);
            StartSync();
        }

        // ---- Create room ----

        private void NewRoomButton_Click(object sender, RoutedEventArgs e)
        {
            OpenAddRoom();
        }

        private void CreateNewRoomButton_Click(object sender, RoutedEventArgs e)
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
