using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using UniMatrix.Services;

namespace UniMatrix
{
    /// <summary>
    /// Settings &gt; Security &gt; "Your sessions": lists every device signed in to the account with a
    /// verified/unverified badge, and lets the user verify (SAS/emoji), rename or remove each one.
    /// Lumia-initiated verification simply calls <see cref="VerificationService.StartAsync"/>; the
    /// existing verification overlay (MainPage.Verify.cs) then drives the emoji comparison.
    /// </summary>
    public sealed partial class MainPage
    {
        private bool _devicesLoading;

        // The device ids of every session other than this one (populated by RefreshDevicesAsync).
        // Used by the bulk "sign out all other sessions" action.
        private readonly List<string> _otherDeviceIds = new List<string>();

        // Cached MAS (next-gen-auth) account session for removing sessions on matrix.org. Created
        // and signed in on demand so the user enters their password at most once per app run.
        private MasClient _mas;

        private async void RefreshDevicesButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshDevicesAsync();
        }

        /// <summary>Opens the full-screen sessions manager and loads the device list on demand.</summary>
        private async void ManageSessionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (SessionsOverlay != null) SessionsOverlay.Visibility = Visibility.Visible;
            await RefreshDevicesAsync();
        }

        private void SessionsCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (SessionsOverlay != null) SessionsOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>Fetches the account's sessions and rebuilds the device-list UI.</summary>
        private async Task RefreshDevicesAsync()
        {
            if (_devicesLoading) return;
            if (DeviceListPanel == null || DeviceListStatus == null) return;
            if (_client == null || string.IsNullOrEmpty(_client.UserId))
            {
                DeviceListStatus.Text = "Sign in to manage your sessions.";
                DeviceListPanel.Children.Clear();
                return;
            }

            _devicesLoading = true;
            DeviceListStatus.Text = "Loading sessions\u2026";
            DeviceListPanel.Children.Clear();
            _otherDeviceIds.Clear();
            if (SignOutOthersButton != null) SignOutOthersButton.Visibility = Visibility.Collapsed;
            try
            {
                // Refresh the locally-tracked device keys first so the verified/unverified badge is
                // accurate (and so a freshly-listed device can be verified right away).
                if (_crypto != null && _crypto.Available)
                {
                    try { await _crypto.UpdateDeviceKeysAsync(new[] { _client.UserId }); }
                    catch (Exception ex) { App.Log("DEVICES: key refresh failed: " + ex.Message); }
                }

                List<DeviceListEntry> devices = await _client.GetDevicesAsync();
                DeviceListPanel.Children.Clear();

                if (devices == null || devices.Count == 0)
                {
                    DeviceListStatus.Text = "No sessions found.";
                    return;
                }

                // Show the current device first, then the rest most-recently-active first.
                string current = _settings != null ? _settings.DeviceId : null;
                devices.Sort((a, b) =>
                {
                    bool ac = a.DeviceId == current, bc = b.DeviceId == current;
                    if (ac != bc) return ac ? -1 : 1;
                    return b.LastSeenTs.CompareTo(a.LastSeenTs);
                });

                DeviceListStatus.Text = devices.Count == 1
                    ? "1 active session."
                    : devices.Count + " active sessions.";

                foreach (var d in devices)
                {
                    bool isCurrent = !string.IsNullOrEmpty(current) && d.DeviceId == current;
                    int trust = 0;
                    if (_crypto != null && _crypto.Available)
                    {
                        var stored = _db.GetDevice(_client.UserId, d.DeviceId);
                        if (stored != null) trust = stored.Trust;
                    }
                    if (!isCurrent && !string.IsNullOrEmpty(d.DeviceId))
                        _otherDeviceIds.Add(d.DeviceId);
                    DeviceListPanel.Children.Add(BuildDeviceCard(d, isCurrent, trust));
                }

                // Offer the bulk action only when there's more than just this session to remove.
                if (SignOutOthersButton != null)
                {
                    SignOutOthersButton.Visibility = _otherDeviceIds.Count > 0
                        ? Visibility.Visible : Visibility.Collapsed;
                    SignOutOthersButton.Content = _otherDeviceIds.Count == 1
                        ? "Sign out 1 other session"
                        : "Sign out all " + _otherDeviceIds.Count + " other sessions";
                }
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: refresh failed: " + ex.Message);
                DeviceListStatus.Text = "Couldn't load your sessions: " + ex.Message;
            }
            finally { _devicesLoading = false; }
        }

        private FrameworkElement BuildDeviceCard(DeviceListEntry d, bool isCurrent, int trust)
        {
            var card = new Border
            {
                Background = Res("AppCardBrush"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8)
            };
            var stack = new StackPanel();

            // Row 1: name + status badge.
            var headerRow = new Grid();
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            string name = string.IsNullOrEmpty(d.DisplayName) ? d.DeviceId : d.DisplayName;
            var nameText = new TextBlock
            {
                Text = name ?? "(unknown session)",
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 15,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(nameText, 0);
            headerRow.Children.Add(nameText);

            var badge = BuildStatusBadge(isCurrent, trust);
            Grid.SetColumn(badge, 1);
            headerRow.Children.Add(badge);
            stack.Children.Add(headerRow);

            // Row 2: device id.
            stack.Children.Add(new TextBlock
            {
                Text = d.DeviceId,
                Foreground = Res("AppFaintTextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            // Row 3: last-seen info.
            string lastSeen = FormatLastSeen(d.LastSeenTs);
            if (!string.IsNullOrEmpty(d.LastSeenIp))
                lastSeen += "  \u00b7  " + d.LastSeenIp;
            stack.Children.Add(new TextBlock
            {
                Text = lastSeen,
                Foreground = Res("AppFaintTextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            // Row 4: action buttons.
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 10, 0, 0)
            };

            string deviceId = d.DeviceId;
            string deviceLabel = name;

            // Verify is only meaningful for *other* sessions that aren't already verified, and only
            // when end-to-end encryption (and hence SAS) is available.
            if (!isCurrent && trust < 1 && _crypto != null && _crypto.Available && _verify != null)
            {
                var verifyBtn = MakeActionButton("Verify", Res("AppAccentBrush"));
                verifyBtn.Click += async (s, e) => await VerifyDeviceClickedAsync(deviceId);
                actions.Children.Add(verifyBtn);
            }

            var renameBtn = MakeActionButton("Rename", Res("AppPanelBrush"));
            renameBtn.Click += async (s, e) => await RenameDeviceClickedAsync(deviceId, d.DisplayName);
            actions.Children.Add(renameBtn);

            if (!isCurrent)
            {
                var removeBtn = MakeActionButton("Remove", new SolidColorBrush(Color.FromArgb(0xFF, 0x5A, 0x1F, 0x1F)));
                removeBtn.Click += async (s, e) => await RemoveDeviceClickedAsync(deviceId, deviceLabel);
                actions.Children.Add(removeBtn);
            }

            stack.Children.Add(actions);
            card.Child = stack;
            return card;
        }

        private FrameworkElement BuildStatusBadge(bool isCurrent, int trust)
        {
            string text;
            Color color;
            if (isCurrent)
            {
                text = "This device";
                color = Color.FromArgb(0xFF, 0x3A, 0x6E, 0xA5);
            }
            else if (trust >= 1)
            {
                text = "Verified \u2713";
                color = Color.FromArgb(0xFF, 0x2E, 0x7D, 0x46);
            }
            else
            {
                text = "Unverified";
                color = Color.FromArgb(0xFF, 0x8A, 0x5A, 0x12);
            }

            return new Border
            {
                Background = new SolidColorBrush(color),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 2, 8, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush(Colors.White),
                    FontSize = 11,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold
                }
            };
        }

        private Button MakeActionButton(string label, Brush background)
        {
            return new Button
            {
                Content = label,
                Background = background,
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 13
            };
        }

        // ---- Actions ----

        private async Task VerifyDeviceClickedAsync(string deviceId)
        {
            if (_verify == null || _crypto == null || !_crypto.Available) return;
            try
            {
                ShowVerifyWorking("Asking the other device to verify\u2026");
                string txn = await _verify.StartAsync(_client.UserId, deviceId);
                if (string.IsNullOrEmpty(txn))
                {
                    CloseVerifyOverlay();
                    await ShowErrorAsync("Couldn't start verification for this session.");
                }
                // From here the VerificationService callbacks (OnShowSas / OnComplete) drive the overlay.
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: verify start failed: " + ex.Message);
                CloseVerifyOverlay();
                await ShowErrorAsync("Couldn't start verification: " + ex.Message);
            }
        }

        private async Task RenameDeviceClickedAsync(string deviceId, string currentName)
        {
            string newName = await PromptInputAsync(
                "Rename session",
                "Give this session a recognisable name.",
                "Session name", currentName, "Save", isPassword: false);
            if (newName == null) return; // cancelled
            newName = newName.Trim();
            if (newName.Length == 0) { await ShowErrorAsync("Enter a name for the session."); return; }

            try
            {
                await _client.RenameDeviceAsync(deviceId, newName);
                await RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: rename failed: " + ex.Message);
                await ShowErrorAsync("Couldn't rename the session: " + ex.Message);
            }
        }

        private async Task RemoveDeviceClickedAsync(string deviceId, string label)
        {
            var confirm = new Windows.UI.Popups.MessageDialog(
                "Sign out and remove \"" + (label ?? deviceId) + "\"? That session will need to sign in again.",
                "Remove session");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Remove"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;
            var choice = await confirm.ShowAsync();
            if (choice == null || choice.Label != "Remove") return;

            // matrix.org and other next-gen-auth (MSC3861/MAS) servers disable the Client-Server
            // device-delete endpoints for app tokens. Use the MAS account API (same thing the web
            // page does) instead; a legacy password DELETE is used on classic homeservers.
            string acctUri = null;
            try { acctUri = await _client.GetAccountManagementUriAsync(); } catch { }
            if (!string.IsNullOrEmpty(acctUri))
            {
                if (!await EnsureMasLoginAsync(acctUri)) return;
                try
                {
                    var sessions = await _mas.ListSessionsAsync();
                    var match = sessions.Find(s => s.DeviceId == deviceId);
                    if (match == null)
                    {
                        await ShowErrorAsync("That session is already gone.");
                    }
                    else if (!await _mas.EndSessionAsync(match))
                    {
                        await ShowErrorAsync("Couldn't remove the session. Check the debug log.");
                    }
                    await RefreshDevicesAsync();
                }
                catch (Exception ex)
                {
                    App.Log("DEVICES: MAS remove failed: " + ex.Message);
                    await ShowErrorAsync("Couldn't remove the session: " + ex.Message);
                }
                return;
            }

            string password = await PromptInputAsync(
                "Confirm it's you",
                "Enter your account password to remove this session.",
                "Password", null, "Remove", isPassword: true);
            if (password == null) return; // cancelled
            if (password.Length == 0) { await ShowErrorAsync("Enter your password to remove the session."); return; }

            try
            {
                await _client.DeleteDeviceAsync(deviceId, password);
                await RefreshDevicesAsync();
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: remove failed: " + ex.Message);
                await ShowErrorAsync("Couldn't remove the session: " + ex.Message);
            }
        }

        private async void SignOutOthersButton_Click(object sender, RoutedEventArgs e)
        {
            // Snapshot the ids so a background refresh can't change the list mid-operation.
            var ids = new List<string>(_otherDeviceIds);
            if (ids.Count == 0) { await ShowErrorAsync("There are no other sessions to sign out."); return; }

            var confirm = new Windows.UI.Popups.MessageDialog(
                "Sign out " + (ids.Count == 1 ? "the other session" : "all " + ids.Count + " other sessions") +
                "? They'll each need to sign in again. This session stays signed in.",
                "Sign out other sessions");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Sign out"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;
            var choice = await confirm.ShowAsync();
            if (choice == null || choice.Label != "Sign out") return;

            // On next-gen-auth servers (matrix.org/MAS) the C-S bulk + per-device delete are disabled
            // for app tokens; use the MAS account API (one password sign-in, then no per-delete prompt).
            string acctUri = null;
            try { acctUri = await _client.GetAccountManagementUriAsync(); } catch { }
            if (!string.IsNullOrEmpty(acctUri))
            {
                await SignOutOthersViaMasAsync(acctUri);
                return;
            }

            string password = await PromptInputAsync(
                "Confirm it's you",
                "Enter your account password to sign out your other sessions.",
                "Password", null, "Sign out", isPassword: true);
            if (password == null) return; // cancelled
            if (password.Length == 0) { await ShowErrorAsync("Enter your password to sign out the sessions."); return; }

            try
            {
                if (SignOutOthersButton != null)
                {
                    SignOutOthersButton.IsEnabled = false;
                    SignOutOthersButton.Content = "Signing out\u2026";
                }
                int removed = await _client.DeleteDevicesAsync(ids, password);
                await RefreshDevicesAsync();
                if (removed < ids.Count)
                    await ShowErrorAsync("Signed out " + removed + " of " + ids.Count +
                        " sessions. The rest couldn't be removed \u2014 check the debug log for details.");
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: bulk remove failed: " + ex.Message);
                await ShowErrorAsync("Couldn't sign out the other sessions: " + ex.Message);
            }
            finally
            {
                if (SignOutOthersButton != null) SignOutOthersButton.IsEnabled = true;
            }
        }

        /// <summary>Signs out every other session through the MAS account API (matrix.org).</summary>
        private async Task SignOutOthersViaMasAsync(string acctUri)
        {
            if (!await EnsureMasLoginAsync(acctUri)) return;

            string current = _settings != null ? _settings.DeviceId : null;
            try
            {
                if (SignOutOthersButton != null)
                {
                    SignOutOthersButton.IsEnabled = false;
                    SignOutOthersButton.Content = "Signing out\u2026";
                }

                var sessions = await _mas.ListSessionsAsync();
                var targets = sessions.FindAll(s => s.DeviceId != current);
                if (targets.Count == 0)
                {
                    await ShowErrorAsync("There are no other sessions to sign out.");
                    await RefreshDevicesAsync();
                    return;
                }

                int removed = 0;
                for (int i = 0; i < targets.Count; i++)
                {
                    try { if (await _mas.EndSessionAsync(targets[i])) removed++; }
                    catch (Exception ex) { App.Log("DEVICES: MAS end failed: " + ex.Message); }
                    if (SignOutOthersButton != null)
                        SignOutOthersButton.Content = "Signing out\u2026 " + (i + 1) + "/" + targets.Count;
                }

                App.Log("DEVICES: MAS bulk removed " + removed + "/" + targets.Count);
                await RefreshDevicesAsync();
                if (removed < targets.Count)
                    await ShowErrorAsync("Signed out " + removed + " of " + targets.Count +
                        " sessions. The rest couldn't be removed \u2014 check the debug log.");
            }
            catch (Exception ex)
            {
                App.Log("DEVICES: MAS bulk failed: " + ex.Message);
                await ShowErrorAsync("Couldn't sign out the other sessions: " + ex.Message);
            }
            finally
            {
                if (SignOutOthersButton != null) SignOutOthersButton.IsEnabled = true;
            }
        }

        // ---- Helpers ----

        /// <summary>
        /// Ensures we have a signed-in MAS account session for next-gen-auth homeservers, prompting
        /// once for the account password. Returns false if the user cancels or sign-in fails (wrong
        /// password, or an SSO/2FA account that can't be driven from the app). The login is cached
        /// in <see cref="_mas"/> so a run of removals only asks for the password once.
        /// </summary>
        private async Task<bool> EnsureMasLoginAsync(string accountUri)
        {
            if (_mas != null && _mas.IsLoggedIn) return true;
            if (_mas == null) _mas = new MasClient();

            // A session cookie from a previous run is often still valid — reuse it silently so we
            // don't ask for the password again.
            try { if (await _mas.TryResumeAsync(accountUri)) return true; }
            catch (Exception ex) { App.Log("DEVICES: MAS resume exc: " + ex.Message); }

            // MAS accepts the localpart or full MXID; derive the localpart from @user:server.
            string username = _client != null ? _client.UserId : null;
            if (!string.IsNullOrEmpty(username) && username.StartsWith("@"))
            {
                int colon = username.IndexOf(':');
                username = colon > 1 ? username.Substring(1, colon - 1) : username.Substring(1);
            }

            string password = await PromptInputAsync(
                "Sign in to manage sessions",
                "matrix.org manages sign-ins on the web. Enter your account password once to remove " +
                "sessions from here \u2014 you won't be asked again for each one. (Won't work for " +
                "SSO or two-factor accounts.)",
                "Password", null, "Sign in", isPassword: true);
            if (password == null) return false; // cancelled
            if (password.Length == 0) { await ShowErrorAsync("Enter your password to continue."); return false; }

            bool ok;
            try { ok = await _mas.LoginAsync(accountUri, username, password); }
            catch (Exception ex) { App.Log("DEVICES: MAS login exc: " + ex.Message); ok = false; }

            if (!ok)
                await ShowErrorAsync("Couldn't sign in. Check your password \u2014 or this account " +
                                     "uses SSO/two-factor sign-in, which can't be managed from the app.");
            return ok;
        }

        /// <summary>Shows a single-field dialog (text or password). Returns the text, or null if cancelled.</summary>
        private async Task<string> PromptInputAsync(string title, string message, string placeholder,
            string initial, string primaryText, bool isPassword)
        {
            var panel = new StackPanel();
            if (!string.IsNullOrEmpty(message))
                panel.Children.Add(new TextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 10)
                });

            Control input;
            if (isPassword)
                input = new PasswordBox { PlaceholderText = placeholder ?? "" };
            else
                input = new TextBox { PlaceholderText = placeholder ?? "", Text = initial ?? "" };
            panel.Children.Add(input);

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = primaryText ?? "OK",
                SecondaryButtonText = "Cancel"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;
            return isPassword ? ((PasswordBox)input).Password : ((TextBox)input).Text;
        }

        private static string FormatLastSeen(long tsMs)
        {
            if (tsMs <= 0) return "Last activity unknown";
            try
            {
                var when = DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime;
                var age = DateTime.Now - when;
                if (age.TotalMinutes < 2) return "Active just now";
                if (age.TotalHours < 1) return "Active " + (int)age.TotalMinutes + " min ago";
                if (age.TotalDays < 1) return "Active " + (int)age.TotalHours + " h ago";
                if (age.TotalDays < 7) return "Active " + (int)age.TotalDays + " d ago";
                return "Last active " + when.ToString("d MMM yyyy");
            }
            catch { return "Last activity unknown"; }
        }

        private static Brush Res(string key)
        {
            return (Brush)Application.Current.Resources[key];
        }
    }
}
