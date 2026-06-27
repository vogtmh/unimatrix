using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using UniMatrix.Models;

namespace UniMatrix
{
    /// <summary>
    /// UI handlers for end-to-end-encryption: enabling encryption on a room, setting up the
    /// server key backup + recovery key, and restoring history from a recovery key/passphrase.
    /// The heavy lifting lives in <see cref="Services.CryptoService"/>,
    /// <see cref="Services.KeyBackupService"/> and <see cref="Services.SsssService"/>.
    /// </summary>
    public sealed partial class MainPage
    {
        // ---- Settings: key-backup status ----

        /// <summary>Updates the ENCRYPTION &amp; BACKUP settings labels/buttons to match current state.</summary>
        private async Task RefreshBackupStatusAsync()
        {
            try
            {
                if (_crypto == null || !_crypto.Available)
                {
                    BackupStatusText.Text = "End-to-end encryption isn't available on this device.";
                    SetupRecoveryButton.IsEnabled = false;
                    EnterRecoveryButton.IsEnabled = false;
                    return;
                }

                if (_backup == null)
                {
                    BackupStatusText.Text = "Key backup status unknown.";
                    return;
                }

                await _backup.LoadAsync();
                if (_backup.Enabled)
                {
                    BackupStatusText.Text = "Key backup is active (version " + _backup.Version + "). Your message keys are backed up.";
                    SetupRecoveryButton.IsEnabled = true;
                    EnterRecoveryButton.IsEnabled = true;
                }
                else if (_backup.ExistsButLocked)
                {
                    BackupStatusText.Text = "A key backup exists on the server. Restore it with your recovery key to read older messages.";
                    SetupRecoveryButton.IsEnabled = true;
                    EnterRecoveryButton.IsEnabled = true;
                }
                else
                {
                    BackupStatusText.Text = "No key backup yet. Set one up so you can recover your encrypted history later.";
                    SetupRecoveryButton.IsEnabled = true;
                    EnterRecoveryButton.IsEnabled = false;
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: RefreshBackupStatusAsync failed: " + ex.Message); }
        }

        // ---- Enable encryption on a room ----

        private async void EnableEncryptionButton_Click(object sender, RoutedEventArgs e)
        {
            var room = Rooms.FirstOrDefault(r => r.Id == _currentRoomId);
            if (room == null) return;
            if (_crypto == null || !_crypto.Available) { await ShowErrorAsync("Encryption isn't available on this device."); return; }
            if (room.IsEncrypted) return;

            var confirm = new Windows.UI.Popups.MessageDialog(
                "Turn on end-to-end encryption for \"" + room.DisplayName + "\"? Once enabled it cannot be turned off.",
                "Enable encryption");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Enable"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;
            var choice = await confirm.ShowAsync();
            if (choice == null || choice.Label != "Enable") return;

            try
            {
                var content = new JsonObject
                {
                    ["algorithm"] = JsonValue.CreateStringValue("m.megolm.v1.aes-sha2")
                };
                string evId = await _client.SendStateEventAsync(room.Id, "m.room.encryption", content);
                if (string.IsNullOrEmpty(evId)) { await ShowErrorAsync("Could not enable encryption."); return; }

                // Reflect immediately; the matching state event will also arrive via /sync.
                _db.SetRoomEncryption(room.Id, "m.megolm.v1.aes-sha2", 604800000, 100);
                room.IsEncrypted = true;
                if (_currentRoomId == room.Id)
                    ChatEncryptedIcon.Visibility = Visibility.Visible;

                InfoEncryptedRow.Visibility = Visibility.Visible;
                EnableEncryptionButton.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not enable encryption: " + ex.Message);
            }
        }

        // ---- Set up key backup + recovery key ----

        private void SetupRecoveryButton_Click(object sender, RoutedEventArgs e)
        {
            SetupPassphraseBox.Password = "";
            RecoveryKeyDisplay.Text = "";
            SecuritySetupInput.Visibility = Visibility.Visible;
            SecuritySetupResult.Visibility = Visibility.Collapsed;
            SecuritySetupProgress.Visibility = Visibility.Collapsed;
            SecuritySetupPanel.Visibility = Visibility.Visible;
        }

        private void SecuritySetupCancel_Click(object sender, RoutedEventArgs e)
        {
            SecuritySetupPanel.Visibility = Visibility.Collapsed;
        }

        private async void SecuritySetupCreate_Click(object sender, RoutedEventArgs e)
        {
            if (_crypto == null || !_crypto.Available || _backup == null || _ssss == null)
            {
                await ShowErrorAsync("Encryption isn't available on this device.");
                return;
            }

            SecuritySetupInput.Visibility = Visibility.Collapsed;
            SecuritySetupProgress.Visibility = Visibility.Visible;
            SecuritySetupProgressText.Text = "Creating backup…";

            try
            {
                // Ensure a server backup exists (this also seeds it with our current keys).
                if (!_backup.Enabled)
                    await _backup.CreateBackupAsync();

                // Set up secret storage; this returns the user-facing recovery key.
                string passphrase = SetupPassphraseBox.Password;
                string recoveryKey = await _ssss.SetUpRecoveryAsync(string.IsNullOrEmpty(passphrase) ? null : passphrase);

                SecuritySetupProgress.Visibility = Visibility.Collapsed;
                if (string.IsNullOrEmpty(recoveryKey))
                {
                    SecuritySetupInput.Visibility = Visibility.Visible;
                    await ShowErrorAsync("Could not set up key backup.");
                    return;
                }

                RecoveryKeyDisplay.Text = recoveryKey;
                SecuritySetupResult.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                SecuritySetupProgress.Visibility = Visibility.Collapsed;
                SecuritySetupInput.Visibility = Visibility.Visible;
                await ShowErrorAsync("Could not set up key backup: " + ex.Message);
            }
        }

        private void RecoveryKeyCopy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var pkg = new Windows.ApplicationModel.DataTransfer.DataPackage();
                pkg.SetText(RecoveryKeyDisplay.Text ?? "");
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(pkg);
            }
            catch (Exception ex) { App.Log("CRYPTO: clipboard copy failed: " + ex.Message); }
        }

        private async void SecuritySetupDone_Click(object sender, RoutedEventArgs e)
        {
            SecuritySetupPanel.Visibility = Visibility.Collapsed;
            await RefreshBackupStatusAsync();
        }

        // ---- Restore from a recovery key / passphrase ----

        private void EnterRecoveryButton_Click(object sender, RoutedEventArgs e)
        {
            RecoveryInput.Text = "";
            RecoveryProgress.Visibility = Visibility.Collapsed;
            RecoveryPanel.Visibility = Visibility.Visible;
        }

        private void RecoveryCancel_Click(object sender, RoutedEventArgs e)
        {
            RecoveryPanel.Visibility = Visibility.Collapsed;
        }

        private async void RecoveryConfirm_Click(object sender, RoutedEventArgs e)
        {
            if (_ssss == null) { await ShowErrorAsync("Encryption isn't available on this device."); return; }

            string input = (RecoveryInput.Text ?? "").Trim();
            if (string.IsNullOrEmpty(input)) { await ShowErrorAsync("Enter your recovery key or passphrase."); return; }

            RecoveryProgress.Visibility = Visibility.Visible;
            RecoveryProgressText.Text = "Restoring…";

            try
            {
                int imported = await _ssss.RecoverAsync(input);
                RecoveryProgress.Visibility = Visibility.Collapsed;

                if (imported < 0)
                {
                    await ShowErrorAsync("Couldn't unlock the backup. Check your recovery key or passphrase.");
                    return;
                }

                RecoveryPanel.Visibility = Visibility.Collapsed;

                // Decrypt any history that the freshly-restored keys can now read.
                RetryAllRoomsDecryption();
                RefreshRooms();
                if (!string.IsNullOrEmpty(_currentRoomId))
                    await LoadMessagesAsync(_currentRoomId);

                await RefreshBackupStatusAsync();
                await ShowErrorAsync("Restored " + imported + " key" + (imported == 1 ? "" : "s") + " from backup.");
            }
            catch (Exception ex)
            {
                RecoveryProgress.Visibility = Visibility.Collapsed;
                await ShowErrorAsync("Restore failed: " + ex.Message);
            }
        }

        /// <summary>Re-attempts decryption of every still-encrypted message across all encrypted rooms
        /// (called after a backup restore brings in new Megolm keys).</summary>
        private void RetryAllRoomsDecryption()
        {
            if (_crypto == null || !_crypto.Available) return;
            try
            {
                foreach (var roomId in _db.GetEncryptedRoomIds())
                    RetryDecryptRoom(roomId);
            }
            catch (Exception ex) { App.Log("CRYPTO: RetryAllRoomsDecryption failed: " + ex.Message); }
        }
    }
}
