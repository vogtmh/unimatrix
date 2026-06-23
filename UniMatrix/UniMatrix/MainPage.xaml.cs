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

        // Background history backfill (pulls full history for every room without the
        // user having to open each one). Self-guarding so it never double-starts.
        private bool _backfillAllActive;
        private CancellationTokenSource _backfillCts;
        private Windows.System.Display.DisplayRequest _displayRequest;
        private readonly object _bfLock = new object();
        private readonly HashSet<string> _backfillInFlight = new HashSet<string>();

        // Live progress for the all-rooms backfill. Updated incrementally as pages arrive
        // (the message counter is bumped on a background thread, so it's read via a UI-thread
        // DispatcherTimer that refreshes the progress bar every few seconds).
        private int _bfDone, _bfTotal;
        private long _bfMessages;
        private DispatcherTimer _bfTimer;
        // Set when the user cancels the backfill; prevents sync passes from auto-restarting it
        // until they change the history setting, wipe the cache, or relaunch.
        private bool _backfillSuppressed;

        // Bound collections.
        public ObservableCollection<Room> Rooms { get; } = new ObservableCollection<Room>();
        public ObservableCollection<Message> Messages { get; } = new ObservableCollection<Message>();

        private const int AvatarThumbSize = 96;
        private const int ImageThumbSize = 320;

        private enum View { Splash, Login, Setup, RoomList, Chat, RoomInfo, Settings }

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
            SetupPanel.Visibility = view == View.Setup ? Visibility.Visible : Visibility.Collapsed;
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

            // Unlimited history -> sinceTs 0 loads everything; otherwise the day window.
            long sinceTs = _settings.HistoryUnlimited
                ? 0
                : DateTimeOffset.UtcNow.AddDays(-_settings.HistoryDays).ToUnixTimeMilliseconds();
            int fallback = PreferencesService.FallbackMessageCount;

            // Render whatever is already cached immediately so the room opens instantly.
            RenderMessages(roomId, sinceTs, fallback);

            // The /sync filter only caches a handful of recent messages per room, so the
            // cache rarely covers the requested window on first open. Backfill from the
            // server when needed, then re-render. This is what makes the day window and
            // "unlimited" actually load older history instead of just the last few.
            if (NeedsBackfill(roomId, sinceTs))
            {
                ShowSyncProgress(true, "Loading messages…");
                try
                {
                    await TryBackfillAsync(roomId, sinceTs, _syncCts?.Token ?? CancellationToken.None);
                }
                finally
                {
                    ShowSyncProgress(false);
                }

                // The user may have navigated away while we paged history.
                if (_currentRoomId == roomId)
                {
                    RenderMessages(roomId, sinceTs, fallback);
                    // Re-render reset the list, so return to the latest message.
                    ScrollMessagesToBottom();
                }
            }
        }

        private void RenderMessages(string roomId, long sinceTs, int fallback)
        {
            var names = _db.GetMemberNames(roomId);
            var msgs = _db.GetMessagesSince(roomId, sinceTs, fallback);

            Messages.Clear();
            DateTime? prevDay = null;
            foreach (var m in msgs)
            {
                DecorateMessage(m, names);
                var day = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).LocalDateTime.Date;
                m.ShowDateSeparator = prevDay == null || day != prevDay.Value;
                prevDay = day;
                Messages.Add(m);
            }

            // Resolve image thumbnails asynchronously (fire and forget).
            foreach (var m in msgs.Where(x => x.IsImage && !string.IsNullOrEmpty(x.Mxc)))
            {
                var _ = ResolveMessageImageAsync(m);
            }
        }

        /// <summary>
        /// True when the cached history doesn't yet reach back to <paramref name="sinceTs"/>
        /// (or, for unlimited, to the start of the room). A per-room "done" flag stops us from
        /// re-paging a room whose history we've already fetched to the beginning.
        /// </summary>
        private bool NeedsBackfill(string roomId, long sinceTs)
        {
            if (_db.GetMeta(BackfillDoneKey(roomId)) == "1") return false;

            long oldest = _db.GetOldestMessageTs(roomId);
            if (oldest == 0) return true;            // nothing cached yet
            return oldest > sinceTs;                 // cache starts after the window -> fetch older
        }

        private static string BackfillDoneKey(string roomId) { return "bf_done_" + roomId; }
        private static string BackfillTokenKey(string roomId) { return "bf_token_" + roomId; }

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

        private async Task<int> TryBackfillAsync(string roomId, long sinceTs, CancellationToken ct, Action<int> onPageStored = null)
        {
            // The /messages limit counts ALL events (state, membership, etc.), so a single
            // page rarely yields enough real messages. Page backward until we've crossed the
            // requested time window, reach the start of the room, or hit a safety cap. The
            // pagination token is persisted so an interrupted backfill (offline / suspend)
            // resumes instead of re-fetching from the top, and a per-room "done" flag prevents
            // re-paging a room whose history we already have in full. Returns the number of
            // new messages stored. Network/DB work runs off the UI thread (ConfigureAwait)
            // since this can run for minutes during the initial all-rooms backfill.
            const int pageSize = 100;
            const int maxPages = 200;
            int fetched = 0;

            // Never backfill the same room from two places at once (e.g. the user opening a
            // room while the background loop is already paging it) — they'd share the token.
            lock (_bfLock) { if (!_backfillInFlight.Add(roomId)) return 0; }
            try
            {
                // Resume from where a previous backfill left off, if any.
                string from = _db.GetMeta(BackfillTokenKey(roomId));

                for (int page = 0; page < maxPages; page++)
                {
                    if (ct.IsCancellationRequested) break;

                    var resp = await _client.GetRoomMessagesAsync(roomId, pageSize, from, ct).ConfigureAwait(false);
                    var chunk = resp != null && resp.ContainsKey("chunk") ? resp.GetNamedArray("chunk") : null;
                    if (chunk == null || chunk.Count == 0)
                    {
                        // Reached the start of the room's history.
                        _db.SetMeta(BackfillDoneKey(roomId), "1");
                        break;
                    }

                    long oldestTs = long.MaxValue;
                    int pageStored = 0;
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
                        fetched++;
                        pageStored++;
                    }

                    // Report this page's new messages so the progress UI can advance mid-room.
                    if (pageStored > 0) onPageStored?.Invoke(pageStored);

                    // "end" is the token for the next (older) page when paging backward.
                    string end = MatrixClient.GetString(resp, "end");
                    if (string.IsNullOrEmpty(end) || end == from)
                    {
                        // No further pages available -> we've reached the start.
                        _db.SetMeta(BackfillDoneKey(roomId), "1");
                        break;
                    }
                    from = end;
                    _db.SetMeta(BackfillTokenKey(roomId), from);

                    // Stop once we've paged past the requested window (unless unlimited).
                    if (sinceTs > 0 && oldestTs != long.MaxValue && oldestTs < sinceTs) break;
                }
            }
            catch (OperationCanceledException) { /* suspended/cancelled: resume next time. */ }
            catch { /* Offline or error: show whatever is cached; resume next time. */ }
            finally
            {
                lock (_bfLock) { _backfillInFlight.Remove(roomId); }
            }
            return fetched;
        }

        private async Task RefreshCurrentRoomMessagesAsync()
        {
            if (_currentRoomId == null) return;
            await LoadMessagesAsync(_currentRoomId);
            ScrollMessagesToBottom();
        }

        private void ScrollMessagesToBottom()
        {
            if (Messages.Count == 0) return;

            // When a room is first opened the ListView hasn't laid out the freshly-added
            // items yet, so an immediate ScrollIntoView lands on the wrong spot (or no-ops).
            // Force a layout pass, scroll, then schedule a second scroll at low priority once
            // virtualization has realized the last item, so we reliably end at the bottom.
            var last = Messages[Messages.Count - 1];
            try
            {
                MessagesList.UpdateLayout();
                MessagesList.ScrollIntoView(last);
            }
            catch { }

            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                try { MessagesList.ScrollIntoView(last); }
                catch { }
            });
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
            // Show a date separator if this echo starts a new day (or the timeline is empty).
            var echoDay = DateTimeOffset.FromUnixTimeMilliseconds(echo.Timestamp).LocalDateTime.Date;
            DateTime? lastDay = Messages.Count > 0
                ? (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(Messages[Messages.Count - 1].Timestamp).LocalDateTime.Date
                : null;
            echo.ShowDateSeparator = lastDay == null || echoDay != lastDay.Value;
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

            // Show the bottom progress bar only for a full initial sync (no token yet),
            // since that's the long one. Incremental long-polls don't need it.
            if (string.IsNullOrEmpty(since)) ShowSyncProgress(true, "Syncing your account…");

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

                    ShowSyncProgress(false);
                    bool wasFirst = _firstSyncTcs?.TrySetResult(true) ?? false;

                    // After the first /sync (or whenever sync brings changes), continue
                    // pulling full history for every room in the background so the user
                    // doesn't have to open each one. Self-guards against double-starting.
                    if (wasFirst || result.HasChanges)
                        StartBackfillAll();
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
                    ShowSyncProgress(false);
                    _firstSyncTcs?.TrySetResult(false);
                    try { await Task.Delay(5000, ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        private void ShowSyncProgress(bool show, string message = null)
        {
            if (SyncProgressPanel == null) return;
            // While the background backfill is running it owns the bottom progress bar;
            // ignore hide requests from individual sync passes / room opens.
            if (!show && _backfillAllActive) return;
            if (show && message != null && SyncProgressText != null)
                SyncProgressText.Text = message;
            SyncProgressPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- Background history backfill (all rooms) ----

        private void StartBackfillAll()
        {
            // All callers run on the UI thread, so this check-and-set is race-free.
            if (_backfillAllActive || _backfillSuppressed) return;
            _backfillAllActive = true;
            _backfillCts = new CancellationTokenSource();
            var ct = _backfillCts.Token;
            var _ = BackfillAllRoomsAsync(ct);
        }

        private void StopBackfillAll()
        {
            try { _backfillCts?.Cancel(); } catch { }
            _backfillCts = null;
        }

        private void SyncCancelButton_Click(object sender, RoutedEventArgs e)
        {
            // User wants to stop a long-running history download (e.g. they picked
            // "unlimited" by mistake). Stop it and don't auto-restart until they change the
            // history setting or wipe the cache. Per-room tokens are persisted, so resuming
            // later picks up where it left off rather than restarting.
            _backfillSuppressed = true;
            StopBackfillAll();
            if (SyncCancelButton != null) SyncCancelButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>Re-enables background backfill after a cancel (e.g. when history settings change).</summary>
        private void ResumeBackfillAll()
        {
            _backfillSuppressed = false;
            StartBackfillAll();
        }

        private async Task BackfillAllRoomsAsync(CancellationToken ct)
        {
            KeepScreenAwake(true);
            StartBackfillTimer();
            try
            {
                var rooms = _db.GetRooms();
                int total = rooms.Count;

                var pending = new List<string>();
                foreach (var r in rooms)
                    if (NeedsBackfill(r.Id, CurrentHistoryWindow())) pending.Add(r.Id);

                if (pending.Count == 0) return;

                _bfTotal = total;
                _bfDone = total - pending.Count;
                _bfMessages = 0;
                UpdateBackfillUi();

                foreach (var roomId in pending)
                {
                    if (ct.IsCancellationRequested) break;

                    long since = CurrentHistoryWindow();
                    // The callback runs on a background thread per page; just bump the
                    // shared counter (the DispatcherTimer paints it on the UI thread).
                    await TryBackfillAsync(roomId, since, ct,
                        delta => System.Threading.Interlocked.Add(ref _bfMessages, delta));
                    _bfDone++;
                    UpdateBackfillUi();

                    // Keep previews fresh, and refresh the open room if we just filled it.
                    if (_activeView == View.RoomList) RefreshRooms();
                    if (_currentRoomId == roomId)
                        RenderMessages(roomId, since, PreferencesService.FallbackMessageCount);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Log("Backfill-all error: " + ex.Message); }
            finally
            {
                StopBackfillTimer();
                _backfillAllActive = false;
                KeepScreenAwake(false);
                HideSyncProgress();
            }
        }

        private void StartBackfillTimer()
        {
            if (_bfTimer == null)
            {
                _bfTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _bfTimer.Tick += (s, e) => UpdateBackfillUi();
            }
            _bfTimer.Start();
        }

        private void StopBackfillTimer()
        {
            try { _bfTimer?.Stop(); } catch { }
        }

        private long CurrentHistoryWindow()
        {
            return _settings.HistoryUnlimited
                ? 0
                : DateTimeOffset.UtcNow.AddDays(-_settings.HistoryDays).ToUnixTimeMilliseconds();
        }

        private void UpdateBackfillUi()
        {
            if (SyncProgressPanel == null) return;
            int done = _bfDone;
            int total = _bfTotal;
            long messages = System.Threading.Interlocked.Read(ref _bfMessages);
            if (SyncProgressText != null)
                SyncProgressText.Text = "Syncing channels " + done + "/" + total +
                                        " · " + messages + " messages so far";
            if (SyncProgressBar != null)
            {
                SyncProgressBar.IsIndeterminate = false;
                SyncProgressBar.Minimum = 0;
                SyncProgressBar.Maximum = Math.Max(1, total);
                SyncProgressBar.Value = done;
            }
            if (SyncCancelButton != null) SyncCancelButton.Visibility = Visibility.Visible;
            SyncProgressPanel.Visibility = Visibility.Visible;
        }

        private void HideSyncProgress()
        {
            if (SyncProgressPanel == null) return;
            SyncProgressPanel.Visibility = Visibility.Collapsed;
            if (SyncProgressBar != null) SyncProgressBar.IsIndeterminate = true;
            if (SyncCancelButton != null) SyncCancelButton.Visibility = Visibility.Collapsed;
        }

        /// <summary>Keeps the display on while a long backfill runs, so it isn't paused by sleep.</summary>
        private void KeepScreenAwake(bool on)
        {
            try
            {
                if (on)
                {
                    if (_displayRequest == null)
                        _displayRequest = new Windows.System.Display.DisplayRequest();
                    _displayRequest.RequestActive();
                }
                else if (_displayRequest != null)
                {
                    _displayRequest.RequestRelease();
                    _displayRequest = null;
                }
            }
            catch { }
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
            // Pause background work and flush so cached history survives termination.
            // Per-room pagination tokens are persisted, so backfill resumes on relaunch.
            try { StopBackfillAll(); } catch { }
            try { StopSync(); } catch { }
            try { KeepScreenAwake(false); } catch { }
            try { _db?.Checkpoint(); } catch { }
        }

        internal void OnAppResuming()
        {
            // Resume sync and pick the background backfill back up where it left off.
            if (!_initialized || _syncProcessor == null) return;
            if (string.IsNullOrEmpty(_settings?.GetAccessToken())) return;
            StartSync();
            StartBackfillAll();
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
