using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using UniMatrix.Data;
using UniMatrix.Models;
using UniMatrix.Services;

namespace UniMatrix
{
    public sealed partial class MainPage : Page
    {
        /// <summary>Singleton reference so App can flush the database on suspend.</summary>
        public static MainPage Current { get; private set; }

        private MatrixDatabase _db;
        private MatrixClient _client;
        private PreferencesService _settings;
        private MediaService _media;
        private SyncProcessor _syncProcessor;

        private CancellationTokenSource _syncCts;
        private string _currentRoomId;
        private bool _initialized;

        // Bound collections.
        public ObservableCollection<Room> Rooms { get; } = new ObservableCollection<Room>();
        public ObservableCollection<Message> Messages { get; } = new ObservableCollection<Message>();

        private const int AvatarThumbSize = 96;
        private const int ImageThumbSize = 320;

        private enum View { Login, RoomList, Chat, RoomInfo, Settings }

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;
            this.Loaded += MainPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_initialized) return;
            _initialized = true;

            _settings = new PreferencesService();
            _client = new MatrixClient();
            _client.SetHomeserver(_settings.Homeserver);

            // Apply the accent preference (system accent by default).
            ThemeService.Apply(_settings.UseSystemAccent);

            SettingsBuild.Text = "Build " + BuildInfo.Date;

            // Open the local cache database.
            string dbPath = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "unimatrix.db");
            _db = new MatrixDatabase(dbPath);
            await _db.OpenAsync();
            _db.CreateSchema();

            _media = new MediaService(_client, _db);

            string token = _settings.GetAccessToken();
            if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(_settings.UserId))
            {
                // Restore an existing session.
                _client.AccessToken = token;
                _syncProcessor = new SyncProcessor(_db, _settings.UserId);
                ShowView(View.RoomList);
                LoadRoomsFromCache();
                StartSync();
            }
            else
            {
                LoginServerBox.Text = _settings.Homeserver;
                ShowView(View.Login);
            }
        }

        // ---- View management ----

        private View _activeView = View.Login;

        private void ShowView(View view)
        {
            _activeView = view;
            LoginPanel.Visibility = view == View.Login ? Visibility.Visible : Visibility.Collapsed;
            RoomListView.Visibility = view == View.RoomList ? Visibility.Visible : Visibility.Collapsed;
            ChatView.Visibility = view == View.Chat ? Visibility.Visible : Visibility.Collapsed;
            RoomInfoPanel.Visibility = view == View.RoomInfo ? Visibility.Visible : Visibility.Collapsed;
            SettingsPanel.Visibility = view == View.Settings ? Visibility.Visible : Visibility.Collapsed;
            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            var nav = SystemNavigationManager.GetForCurrentView();
            bool canGoBack = _activeView == View.Chat || _activeView == View.RoomInfo || _activeView == View.Settings;
            nav.AppViewBackButtonVisibility = canGoBack
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Collapsed;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            switch (_activeView)
            {
                case View.Chat:
                    e.Handled = true;
                    CloseRoom();
                    break;
                case View.RoomInfo:
                    e.Handled = true;
                    ShowView(View.Chat);
                    break;
                case View.Settings:
                    e.Handled = true;
                    ShowView(View.RoomList);
                    break;
            }
        }

        // ---- Room list ----

        private void LoadRoomsFromCache()
        {
            var rooms = _db.GetRooms();
            MergeRooms(rooms);
        }

        private void RefreshRooms()
        {
            MergeRooms(_db.GetRooms());
        }

        /// <summary>Reconciles the bound Rooms collection with the database without full rebuilds.</summary>
        private void MergeRooms(List<Room> rooms)
        {
            var byId = new Dictionary<string, Room>();
            foreach (var r in Rooms) byId[r.Id] = r;

            // Update or insert in sorted order.
            for (int i = 0; i < rooms.Count; i++)
            {
                var incoming = rooms[i];
                ResolveRoomAvatar(incoming);

                Room existing;
                if (byId.TryGetValue(incoming.Id, out existing))
                {
                    existing.Name = incoming.Name;
                    existing.Topic = incoming.Topic;
                    existing.MemberCount = incoming.MemberCount;
                    existing.UnreadCount = incoming.UnreadCount;
                    existing.LastEventTs = incoming.LastEventTs;
                    existing.LastPreview = incoming.LastPreview;
                    if (existing.AvatarUrl != incoming.AvatarUrl)
                    {
                        existing.AvatarMxc = incoming.AvatarMxc;
                        existing.AvatarUrl = incoming.AvatarUrl;
                    }
                    int currentIndex = Rooms.IndexOf(existing);
                    if (currentIndex != i && i < Rooms.Count)
                        Rooms.Move(currentIndex, i);
                }
                else
                {
                    if (i <= Rooms.Count) Rooms.Insert(i, incoming);
                    else Rooms.Add(incoming);
                }
            }

            // Remove rooms no longer present.
            var keep = new HashSet<string>(rooms.Select(r => r.Id));
            for (int i = Rooms.Count - 1; i >= 0; i--)
            {
                if (!keep.Contains(Rooms[i].Id)) Rooms.RemoveAt(i);
            }

            RoomsEmpty.Visibility = Rooms.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResolveRoomAvatar(Room room)
        {
            if (!string.IsNullOrEmpty(room.AvatarMxc))
                room.AvatarUrl = _client.ResolveThumbnailUrl(room.AvatarMxc, AvatarThumbSize);
        }

        private void RoomsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var room = e.ClickedItem as Room;
            if (room != null) OpenRoom(room);
        }

        // ---- Chat ----

        private async void OpenRoom(Room room)
        {
            _currentRoomId = room.Id;
            ChatRoomName.Text = room.DisplayName;
            ChatRoomMembers.Text = room.MemberText;

            // Clear unread locally and in cache.
            if (room.UnreadCount > 0)
            {
                room.UnreadCount = 0;
                _db.SetRoomUnread(room.Id, 0);
            }

            ShowView(View.Chat);
            await LoadMessagesAsync(room.Id);
            ScrollMessagesToBottom();
        }

        private async Task LoadMessagesAsync(string roomId)
        {
            Messages.Clear();

            var names = _db.GetMemberNames(roomId);
            var msgs = _db.GetMessages(roomId, _settings.MessageLimit);

            // Backfill from the server if we have no cached history yet.
            if (msgs.Count == 0)
            {
                await TryBackfillAsync(roomId);
                msgs = _db.GetMessages(roomId, _settings.MessageLimit);
                names = _db.GetMemberNames(roomId);
            }

            foreach (var m in msgs)
            {
                DecorateMessage(m, names);
                Messages.Add(m);
            }

            // Resolve image thumbnails asynchronously.
            foreach (var m in msgs.Where(x => x.IsImage && !string.IsNullOrEmpty(x.Mxc)))
            {
                await ResolveMessageImageAsync(m);
            }
        }

        private void DecorateMessage(Message m, Dictionary<string, string> names)
        {
            m.IsMine = m.Sender == _settings.UserId;
            string display;
            if (m.Sender != null && names.TryGetValue(m.Sender, out display))
                m.SenderDisplay = display;
        }

        private async Task ResolveMessageImageAsync(Message m)
        {
            try
            {
                string uri = await _media.GetThumbnailUriAsync(m.Mxc, ImageThumbSize);
                if (uri != null) m.MediaUrl = uri;
            }
            catch { }
        }

        private async Task TryBackfillAsync(string roomId)
        {
            try
            {
                var resp = await _client.GetRoomMessagesAsync(roomId, _settings.MessageLimit, CancellationToken.None);
                // Reuse the sync processor's event parsing by wrapping chunk into a timeline.
                var chunk = resp != null && resp.ContainsKey("chunk") ? resp.GetNamedArray("chunk") : null;
                if (chunk == null) return;

                foreach (var evVal in chunk)
                {
                    var ev = evVal.GetObject();
                    string type = MatrixClient.GetString(ev, "type");
                    if (type != "m.room.message") continue;

                    string eventId = MatrixClient.GetString(ev, "event_id");
                    if (string.IsNullOrEmpty(eventId) || _db.MessageExists(eventId)) continue;

                    var content = ev.ContainsKey("content") ? ev.GetNamedObject("content") : null;
                    if (content == null) continue;
                    string msgType = MatrixClient.GetString(content, "msgtype");
                    if (msgType != "m.text" && msgType != "m.notice" && msgType != "m.image") continue;

                    _db.UpsertMessage(new Message
                    {
                        EventId = eventId,
                        RoomId = roomId,
                        Sender = MatrixClient.GetString(ev, "sender"),
                        MsgType = msgType,
                        Body = MatrixClient.GetString(content, "body"),
                        Timestamp = (long)(ev.ContainsKey("origin_server_ts") ? ev.GetNamedNumber("origin_server_ts") : 0),
                        Mxc = msgType == "m.image" ? MatrixClient.GetString(content, "url") : null,
                        IsLocalEcho = false
                    });
                }
            }
            catch { /* Offline or error: show whatever is cached. */ }
        }

        private async Task RefreshCurrentRoomMessagesAsync()
        {
            if (_currentRoomId == null) return;
            await LoadMessagesAsync(_currentRoomId);
            ScrollMessagesToBottom();
        }

        private void ScrollMessagesToBottom()
        {
            if (Messages.Count > 0)
                MessagesList.ScrollIntoView(Messages[Messages.Count - 1]);
        }

        private void CloseRoom()
        {
            _currentRoomId = null;
            Messages.Clear();
            ShowView(View.RoomList);
        }

        private void ChatBackButton_Click(object sender, RoutedEventArgs e) => CloseRoom();

        // ---- Sending ----

        private void MessageBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SendCurrentMessage();
            }
        }

        private void SendButton_Click(object sender, RoutedEventArgs e) => SendCurrentMessage();

        private async void SendCurrentMessage()
        {
            string text = MessageBox.Text?.Trim();
            if (string.IsNullOrEmpty(text) || _currentRoomId == null) return;

            string roomId = _currentRoomId;
            MessageBox.Text = "";

            // Optimistic local echo.
            var echo = new Message
            {
                EventId = "echo_" + Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                Sender = _settings.UserId,
                MsgType = "m.text",
                Body = text,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsLocalEcho = true,
                IsMine = true
            };
            _db.UpsertMessage(echo);
            Messages.Add(echo);
            ScrollMessagesToBottom();

            try
            {
                await _client.SendTextMessageAsync(roomId, text);
                // The confirmed event arrives via /sync, which removes this echo.
            }
            catch (Exception ex)
            {
                echo.Body = text + "  (not sent)";
                await ShowErrorAsync("Could not send message: " + ex.Message);
            }
        }

        // ---- Room info ----

        private void RoomInfoButton_Click(object sender, RoutedEventArgs e)
        {
            var room = Rooms.FirstOrDefault(r => r.Id == _currentRoomId);
            if (room == null) return;

            InfoRoomName.Text = room.DisplayName;
            InfoMembers.Text = room.MemberText;
            InfoTopic.Text = room.Topic ?? "";
            InfoTopic.Visibility = string.IsNullOrEmpty(room.Topic) ? Visibility.Collapsed : Visibility.Visible;
            InfoRoomId.Text = room.Id;

            InfoAvatarInitial.Text = room.AvatarInitial;
            InfoAvatarFallback.Fill = room.AvatarBrush;
            if (room.HasAvatar)
            {
                InfoAvatarImage.Fill = new ImageBrush
                {
                    ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(room.AvatarUrl)),
                    Stretch = Stretch.UniformToFill
                };
                InfoAvatarImage.Visibility = Visibility.Visible;
                InfoAvatarInitial.Visibility = Visibility.Collapsed;
            }
            else
            {
                InfoAvatarImage.Visibility = Visibility.Collapsed;
                InfoAvatarInitial.Visibility = Visibility.Visible;
            }

            ShowView(View.RoomInfo);
        }

        private void RoomInfoCloseButton_Click(object sender, RoutedEventArgs e) => ShowView(View.Chat);

        // ---- Sync loop ----

        private void StartSync()
        {
            StopSync();
            _syncCts = new CancellationTokenSource();
            var ct = _syncCts.Token;
            var _ = SyncLoopAsync(ct);
        }

        private void StopSync()
        {
            try { _syncCts?.Cancel(); } catch { }
            _syncCts = null;
        }

        private async Task SyncLoopAsync(CancellationToken ct)
        {
            string since = _db.GetMeta("next_batch");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    SetSyncLed(Colors.Orange);
                    var resp = await _client.SyncAsync(since, 30000, ct);
                    if (ct.IsCancellationRequested) break;

                    var result = _syncProcessor.Process(resp);
                    if (!string.IsNullOrEmpty(result.NextBatch))
                    {
                        since = result.NextBatch;
                        _db.SetMeta("next_batch", since);
                    }

                    SetSyncLed(Color.FromArgb(255, 0x4C, 0xD9, 0x64)); // green
                    ClearSyncError();

                    if (result.HasChanges)
                    {
                        if (_activeView == View.RoomList || _activeView == View.Chat)
                            RefreshRooms();
                        if (_currentRoomId != null && result.ChangedRooms.Contains(_currentRoomId))
                            await RefreshCurrentRoomMessagesAsync();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    App.Log("SYNC ERROR (since=" + (since ?? "<initial>") + "): " + ex);
                    SetSyncLed(Color.FromArgb(255, 0xFF, 0x6B, 0x6B)); // red
                    ShowSyncError(ex.Message);
                    try { await Task.Delay(5000, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private void SetSyncLed(Color color)
        {
            SyncLed.Fill = new SolidColorBrush(color);
        }

        private void ShowSyncError(string message)
        {
            if (SyncErrorText == null) return;
            // Only useful while the list is empty; otherwise the LED conveys the state.
            if (Rooms.Count == 0)
            {
                SyncErrorText.Text = "Sync failed: " + message;
                SyncErrorText.Visibility = Visibility.Visible;
            }
        }

        private void ClearSyncError()
        {
            if (SyncErrorText == null) return;
            SyncErrorText.Visibility = Visibility.Collapsed;
        }

        // ---- Lifecycle ----

        internal void OnAppSuspending()
        {
            try { _db?.Checkpoint(); } catch { }
        }

        private async Task ShowErrorAsync(string message)
        {
            try
            {
                var dialog = new Windows.UI.Popups.MessageDialog(message, "UniMatrix");
                await dialog.ShowAsync();
            }
            catch { }
        }
    }
}
