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

        /// <summary>Signals completion of the first /sync pass so the splash can dismiss.</summary>
        private TaskCompletionSource<bool> _firstSyncTcs;

        // Bound collections.
        public ObservableCollection<Room> Rooms { get; } = new ObservableCollection<Room>();
        public ObservableCollection<Message> Messages { get; } = new ObservableCollection<Message>();

        private const int AvatarThumbSize = 96;
        private const int ImageThumbSize = 320;

        private enum View { Splash, Login, RoomList, Chat, RoomInfo, Settings }

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;
            this.Loaded += MainPage_Loaded;
            App.LogLine += OnLogLine;
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
                // Restore an existing session without flashing the login form.
                _client.AccessToken = token;
                _syncProcessor = new SyncProcessor(_db, _settings.UserId);

                ShowView(View.Splash);
                SplashFadeIn.Begin();
                SplashPulse.Begin();

                LoadRoomsFromCache();

                _firstSyncTcs = new TaskCompletionSource<bool>();
                StartSync();

                // Wait for the first sync pass, but don't hang forever on a slow/offline network.
                await Task.WhenAny(_firstSyncTcs.Task, Task.Delay(8000));

                SplashPulse.Stop();
                ShowView(View.RoomList);
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
            SplashPanel.Visibility = view == View.Splash ? Visibility.Visible : Visibility.Collapsed;
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

                Room existing;
                if (byId.TryGetValue(incoming.Id, out existing))
                {
                    existing.Name = incoming.Name;
                    existing.Topic = incoming.Topic;
                    existing.MemberCount = incoming.MemberCount;
                    existing.UnreadCount = incoming.UnreadCount;
                    existing.LastEventTs = incoming.LastEventTs;
                    existing.LastPreview = incoming.LastPreview;
                    if (existing.AvatarMxc != incoming.AvatarMxc)
                    {
                        // Avatar changed: drop the resolved URL so it gets re-fetched.
                        existing.AvatarMxc = incoming.AvatarMxc;
                        existing.AvatarUrl = null;
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

            // Download avatars (and cache them) for rooms that have one but haven't
            // resolved it yet. On failure the room keeps its colored initial fallback.
            var _ = LoadRoomAvatarsAsync();
        }

        /// <summary>
        /// Downloads and caches room avatars via the media service. Only sets AvatarUrl
        /// when the download succeeds, so rooms whose avatar can't be fetched keep their
        /// generated initial avatar instead of showing a blank circle.
        /// </summary>
        private async Task LoadRoomAvatarsAsync()
        {
            var pending = Rooms
                .Where(r => !string.IsNullOrEmpty(r.AvatarMxc) && string.IsNullOrEmpty(r.AvatarUrl))
                .ToList();

            foreach (var room in pending)
            {
                try
                {
                    string uri = await _media.GetThumbnailUriAsync(room.AvatarMxc, AvatarThumbSize);
                    if (!string.IsNullOrEmpty(uri))
                    {
                        room.AvatarUrl = uri;
                    }
                    else
                    {
                        App.Log("Avatar failed for '" + room.DisplayName + "' (using initial)");
                    }
                }
                catch (Exception ex)
                {
                    App.Log("Avatar EXC '" + room.DisplayName + "': " + ex.Message);
                }
            }
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

            long sinceTs = DateTimeOffset.UtcNow
                .AddDays(-_settings.HistoryDays).ToUnixTimeMilliseconds();
            int fallback = PreferencesService.FallbackMessageCount;
            int maxCount = PreferencesService.MaxMessagesPerRoom;

            var names = _db.GetMemberNames(roomId);
            var msgs = _db.GetMessagesSince(roomId, sinceTs, fallback, maxCount);

            // Backfill from the server if we have no cached history yet.
            if (msgs.Count == 0)
            {
                await TryBackfillAsync(roomId, sinceTs);
                msgs = _db.GetMessagesSince(roomId, sinceTs, fallback, maxCount);
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

        private async Task TryBackfillAsync(string roomId, long sinceTs)
        {
            // The /messages limit counts ALL events (state, membership, etc.), so a single
            // page rarely yields enough real messages. Page backward until we've crossed the
            // requested time window (and have at least one message), run out of history, or
            // hit a safety cap that protects memory on quiet rooms with old-only history.
            const int pageSize = 60;
            const int maxPages = 12;
            try
            {
                string from = null;
                int realMessages = 0;

                for (int page = 0; page < maxPages; page++)
                {
                    var resp = await _client.GetRoomMessagesAsync(roomId, pageSize, from, CancellationToken.None);
                    var chunk = resp != null && resp.ContainsKey("chunk") ? resp.GetNamedArray("chunk") : null;
                    if (chunk == null || chunk.Count == 0) break;

                    long oldestTs = long.MaxValue;
                    foreach (var evVal in chunk)
                    {
                        var ev = evVal.GetObject();
                        long ts = (long)(ev.ContainsKey("origin_server_ts") ? ev.GetNamedNumber("origin_server_ts") : 0);
                        if (ts > 0 && ts < oldestTs) oldestTs = ts;

                        string type = MatrixClient.GetString(ev, "type");
                        if (type != "m.room.message") continue;

                        string eventId = MatrixClient.GetString(ev, "event_id");
                        if (string.IsNullOrEmpty(eventId)) continue;

                        var content = ev.ContainsKey("content") ? ev.GetNamedObject("content") : null;
                        if (content == null) continue;
                        string msgType = MatrixClient.GetString(content, "msgtype");
                        if (msgType != "m.text" && msgType != "m.notice" && msgType != "m.image") continue;

                        realMessages++;
                        if (_db.MessageExists(eventId)) continue;

                        _db.UpsertMessage(new Message
                        {
                            EventId = eventId,
                            RoomId = roomId,
                            Sender = MatrixClient.GetString(ev, "sender"),
                            MsgType = msgType,
                            Body = MatrixClient.GetString(content, "body"),
                            Timestamp = ts,
                            Mxc = msgType == "m.image" ? MatrixClient.GetString(content, "url") : null,
                            IsLocalEcho = false
                        });
                    }

                    // Stop once we've covered the time window and have something to show.
                    bool coveredWindow = oldestTs != long.MaxValue && oldestTs < sinceTs;
                    if (coveredWindow && realMessages > 0) break;

                    // Otherwise page further back; "end" is the token for older events (dir=b).
                    string end = MatrixClient.GetString(resp, "end");
                    if (string.IsNullOrEmpty(end) || end == from) break;
                    from = end;
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

            // If we have a sync token but no cached rooms, a previous run saved the
            // token without persisting the room list. An incremental sync from that
            // token only returns deltas (nothing), so the list would stay empty
            // forever. Force a full initial sync to repopulate.
            if (!string.IsNullOrEmpty(since) && _db.GetRooms().Count == 0)
            {
                App.Log("Have next_batch but 0 cached rooms -> forcing full initial sync.");
                since = null;
                _db.SetMeta("next_batch", "");
            }

            App.Log("Sync loop started. since=" + (since ?? "<initial>") +
                    " homeserver=" + _client.BaseUrl);

            // Orange = connecting / initial sync in progress (we have no live data yet).
            // Once a pass succeeds we go green and STAY green: incremental /sync is a
            // long-poll that holds the connection open until something changes, which
            // is normal and means we are connected, not busy.
            SetSyncLed(Colors.Orange);
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var resp = await _client.SyncAsync(since, 30000, ct);
                    if (ct.IsCancellationRequested) break;

                    // Persist on a background thread: an initial sync writes many
                    // rooms/members and would otherwise freeze the UI thread,
                    // leaving the sync LED stuck on orange.
                    var result = await Task.Run(() => _syncProcessor.Process(resp), ct);
                    if (ct.IsCancellationRequested) break;
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

                    _firstSyncTcs?.TrySetResult(true);
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
                    _firstSyncTcs?.TrySetResult(false);
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

        // ---- Debug overlay ----

        private readonly System.Text.StringBuilder _debugBuffer = new System.Text.StringBuilder();
        private const int DebugMaxChars = 20000;

        /// <summary>Receives every App.Log line and appends it to the on-screen overlay.</summary>
        private async void OnLogLine(string line)
        {
            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher == null) return;
                await dispatcher.RunAsync(CoreDispatcherPriority.Low, () => AppendDebugLine(line));
            }
            catch { }
        }

        private void AppendDebugLine(string line)
        {
            if (DebugText == null) return;
            _debugBuffer.Append(line).Append('\n');
            if (_debugBuffer.Length > DebugMaxChars)
                _debugBuffer.Remove(0, _debugBuffer.Length - DebugMaxChars);
            DebugText.Text = _debugBuffer.ToString();
            // Auto-scroll to the newest line.
            DebugScroll?.ChangeView(null, DebugScroll.ScrollableHeight, null, true);
        }

        private void DebugToggle_Click(object sender, RoutedEventArgs e)
        {
            DebugOverlay.Visibility = DebugOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void DebugClear_Click(object sender, RoutedEventArgs e)
        {
            _debugBuffer.Clear();
            if (DebugText != null) DebugText.Text = "";
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
