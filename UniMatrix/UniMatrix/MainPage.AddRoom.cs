using System;
using System.Threading;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using UniMatrix.Models;

namespace UniMatrix
{
    public sealed partial class MainPage
    {
        // ---- Add room (browse directory / join by address) ----

        private void OpenAddRoom()
        {
            JoinAddressBox.Text = "";
            DirectMessageBox.Text = "";
            DirectorySearchBox.Text = "";
            PublicRooms.Clear();
            SetAddRoomStatus(null, false);
            ShowView(View.AddRoom);

            // Populate the list with popular rooms straight away so "browse" is one tap.
            var ignore = DoDirectorySearchAsync(null);
        }

        private void AddRoomCloseButton_Click(object sender, RoutedEventArgs e)
        {
            ShowView(View.RoomList);
        }

        private void DirectMessageBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                StartDirectChatButton_Click(sender, null);
            }
        }

        private async void StartDirectChatButton_Click(object sender, RoutedEventArgs e)
        {
            string userId = DirectMessageBox.Text?.Trim();
            if (string.IsNullOrEmpty(userId))
            {
                SetAddRoomStatus("Enter a user ID, e.g. @alice:matrix.org", true);
                return;
            }
            if (userId[0] != '@' || !userId.Contains(":"))
            {
                SetAddRoomStatus("User ID must look like @alice:matrix.org", true);
                return;
            }

            AddRoomProgress.IsActive = true;
            SetAddRoomStatus("Starting chat with " + userId + "\u2026", false);
            try
            {
                string roomId = await _client.CreateDirectChatAsync(userId);
                if (string.IsNullOrEmpty(roomId))
                {
                    SetAddRoomStatus("Could not start chat: no room was created.", true);
                    return;
                }

                // The room normally appears on the next /sync, but jump straight in: persist a
                // minimal placeholder (named after the invitee until membership syncs) so it shows
                // in the list and can be opened now, then refresh + open it with a short animation.
                var placeholder = new Room { Id = roomId, Name = DirectChatLocalPart(userId), IsDirect = true };
                _db.UpsertRoom(placeholder);
                _db.SetRoomDirect(roomId, true);
                RefreshRooms();

                Room room = null;
                foreach (var r in Rooms) { if (r.Id == roomId) { room = r; break; } }
                if (room == null) room = placeholder;

                OpenRoomAnimated(room);
            }
            catch (Exception ex)
            {
                SetAddRoomStatus("Could not start chat: " + ex.Message, true);
            }
            finally
            {
                AddRoomProgress.IsActive = false;
            }
        }

        /// <summary>"@alice:matrix.org" -&gt; "alice" for a provisional direct-chat title.</summary>
        private static string DirectChatLocalPart(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return userId;
            string s = userId[0] == '@' ? userId.Substring(1) : userId;
            int colon = s.IndexOf(':');
            return colon > 0 ? s.Substring(0, colon) : s;
        }

        private void DirectorySearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                DirectorySearchButton_Click(sender, null);
            }
        }

        private async void DirectorySearchButton_Click(object sender, RoutedEventArgs e)
        {
            await DoDirectorySearchAsync(DirectorySearchBox.Text);
        }

        private async System.Threading.Tasks.Task DoDirectorySearchAsync(string term)
        {
            PublicRooms.Clear();
            AddRoomProgress.IsActive = true;
            SetAddRoomStatus("Loading public rooms…", false);
            try
            {
                // Browse the user's own homeserver directory.
                var rooms = await _client.GetPublicRoomsAsync(null, term, CancellationToken.None);
                foreach (var r in rooms) PublicRooms.Add(r);

                if (PublicRooms.Count == 0)
                    SetAddRoomStatus("No public rooms found.", false);
                else
                    SetAddRoomStatus(null, false);
            }
            catch (Exception ex)
            {
                SetAddRoomStatus("Could not load rooms: " + ex.Message, true);
            }
            finally
            {
                AddRoomProgress.IsActive = false;
            }
        }

        private void JoinAddressBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                JoinAddressButton_Click(sender, null);
            }
        }

        private async void JoinAddressButton_Click(object sender, RoutedEventArgs e)
        {
            string target = JoinAddressBox.Text?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                SetAddRoomStatus("Enter a room address, e.g. #room:matrix.org", true);
                return;
            }
            if (target[0] != '#' && target[0] != '!')
            {
                SetAddRoomStatus("Address must start with # (alias) or ! (room id).", true);
                return;
            }
            await JoinRoomAsync(target);
        }

        private async void PublicRoomsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var entry = e.ClickedItem as PublicRoomEntry;
            if (entry == null) return;
            await JoinRoomAsync(entry.JoinTarget, entry.DisplayName);
        }

        private async System.Threading.Tasks.Task JoinRoomAsync(string target, string displayName = null)
        {
            AddRoomProgress.IsActive = true;
            SetAddRoomStatus("Joining " + (displayName ?? target) + "…", false);
            try
            {
                await _client.JoinRoomAsync(target);
                // The room shows up on the next /sync pass; return to the list.
                ShowView(View.RoomList);
            }
            catch (Exception ex)
            {
                SetAddRoomStatus("Could not join: " + ex.Message, true);
            }
            finally
            {
                AddRoomProgress.IsActive = false;
            }
        }

        private void SetAddRoomStatus(string text, bool isError)
        {
            if (string.IsNullOrEmpty(text))
            {
                AddRoomStatus.Visibility = Visibility.Collapsed;
                return;
            }
            AddRoomStatus.Text = text;
            AddRoomStatus.Foreground = isError
                ? new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFF, 0x6B, 0x6B))
                : (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["AppSubtleTextBrush"];
            AddRoomStatus.Visibility = Visibility.Visible;
        }
    }
}
