using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
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
        private CallService _callService;

        private CancellationTokenSource _syncCts;
        private string _currentRoomId;
        private bool _initialized;

        // When the app is launched (or activated) by tapping a message toast, the room id to open
        // is stashed here until the room list is ready, then opened.
        private string _pendingLaunchRoomId;

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
        public ObservableCollection<PublicRoomEntry> PublicRooms { get; } = new ObservableCollection<PublicRoomEntry>();

        private const int AvatarThumbSize = 96;
        private const int ImageThumbSize = 320;

        // Scroll-back lazy loading: keep only a sliding window of messages in memory so a room
        // with tens of thousands of messages doesn't bloat memory. The full history stays in the
        // on-disk DB; older pages are pulled in as the user scrolls to the top.
        private const int MessagePageSize = 50;
        private ScrollViewer _messagesScrollViewer;
        private bool _loadingOlder;
        private bool _hasMoreOlder;

        // Timing instrumentation for the room-open path. Restarted in OpenRoom so every PERF
        // log line is relative to the moment the user tapped the room (ms since open).
        private readonly System.Diagnostics.Stopwatch _openWatch = new System.Diagnostics.Stopwatch();

        private enum View { Splash, Login, Setup, RoomList, Chat, RoomInfo, Settings, AddRoom, Invite }

        public MainPage()
        {
            this.InitializeComponent();
            Current = this;
            this.Loaded += MainPage_Loaded;
            App.LogLine += OnLogLine;

            // Keep the focused login field above the on-screen keyboard. The soft keyboard
            // occludes the bottom of the screen, so shrink the login scroll area to the space
            // above it; the ScrollViewer then brings the focused field into view automatically.
            var inputPane = InputPane.GetForCurrentView();
            inputPane.Showing += InputPane_Showing;
            inputPane.Hiding += InputPane_Hiding;
        }

        private void InputPane_Showing(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            if (LoginScroll != null)
            {
                LoginScroll.Margin = new Thickness(0, 0, 0, args.OccludedRect.Height);
            }
            if (ChatView != null)
            {
                // Lift the whole chat (composer + message list) above the keyboard.
                ChatView.Margin = new Thickness(0, 0, 0, args.OccludedRect.Height);
                ScrollMessagesToBottom();
            }
            args.EnsuredFocusedElementInView = true;
        }

        private void InputPane_Hiding(InputPane sender, InputPaneVisibilityEventArgs args)
        {
            if (LoginScroll != null)
            {
                LoginScroll.Margin = new Thickness(0);
            }
            if (ChatView != null)
            {
                ChatView.Margin = new Thickness(0);
            }
            args.EnsuredFocusedElementInView = true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

            // A toast launch passes its argument ("room=<id>") here as the navigation parameter.
            string roomId = ParseRoomLaunchArg(e.Parameter as string);
            if (!string.IsNullOrEmpty(roomId)) _pendingLaunchRoomId = roomId;
        }

        /// <summary>Extracts the room id from a toast launch argument ("room=&lt;id&gt;"), or null.</summary>
        private static string ParseRoomLaunchArg(string arg)
        {
            if (string.IsNullOrEmpty(arg)) return null;
            const string prefix = "room=";
            if (arg.StartsWith(prefix, StringComparison.Ordinal))
            {
                string id = arg.Substring(prefix.Length);
                return string.IsNullOrEmpty(id) ? null : id;
            }
            return null;
        }

        /// <summary>
        /// Handles a toast tap while the app is already running: opens the referenced room
        /// immediately if the room list is ready, otherwise stashes it for after initialization.
        /// </summary>
        public void HandleToastLaunch(string arg)
        {
            string roomId = ParseRoomLaunchArg(arg);
            if (string.IsNullOrEmpty(roomId)) return;

            if (_initialized) OpenRoomById(roomId);
            else _pendingLaunchRoomId = roomId;
        }

        /// <summary>Opens a room by id, preferring the bound instance and falling back to the cache.</summary>
        private void OpenRoomById(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;

            Room room = null;
            foreach (var r in Rooms)
            {
                if (r.Id == roomId) { room = r; break; }
            }
            if (room == null) room = _db.GetRoom(roomId);
            if (room == null) return;

            OpenRoom(room);
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

            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            SettingsBuild.Text = "Version " + v.Major + "." + v.Minor + "." + v.Build + "." + v.Revision +
                                 "  \u2022  Build " + BuildInfo.Date;

            // Open the local cache database.
            string dbPath = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "unimatrix.db");
            _db = new MatrixDatabase(dbPath);
            await _db.OpenAsync();
            _db.CreateSchema();

            _media = new MediaService(_client, _db);

            // WebRTC audio calling. WebRTC requires a CoreDispatcher (its event queue binds to the
            // UI thread) so it is created here rather than in a service constructor.
            _callService = new CallService();
            _callService.Initialize(Dispatcher, _client);
            _callService.IncomingCall += CallService_IncomingCall;
            _callService.CallConnected += CallService_CallConnected;
            _callService.CallEnded += CallService_CallEnded;

            string token = _settings.GetAccessToken();
            if (!string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(_settings.UserId))
            {
                // Restore an existing session without flashing the login form.
                _client.AccessToken = token;
                _client.UserId = _settings.UserId;
                _syncProcessor = new SyncProcessor(_db, _settings.UserId);

                // The splash only needs to cover the brief local DB/cache load so the login form
                // never flashes. It stays visible (background only, no pulsing animation) for the
                // few milliseconds until the cached rooms are ready.
                ShowView(View.Splash);

                LoadRoomsFromCache();

                _firstSyncTcs = new TaskCompletionSource<bool>();
                StartSync();

                // Cached rooms are already populated, so show them immediately and let the first
                // /sync finish in the background (the sync LED shows its progress). No need to sit
                // on a "Logging in…" animation waiting for the network.
                ShowView(View.RoomList);

                // Keep the periodic message-notification background task registered while signed in.
                var _ = NotificationTask.RegisterAsync();

                // If we were launched by tapping a message toast, open that room now.
                if (!string.IsNullOrEmpty(_pendingLaunchRoomId))
                {
                    string pending = _pendingLaunchRoomId;
                    _pendingLaunchRoomId = null;
                    OpenRoomById(pending);
                }
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
            AddRoomPanel.Visibility = view == View.AddRoom ? Visibility.Visible : Visibility.Collapsed;
            InvitePanel.Visibility = view == View.Invite ? Visibility.Visible : Visibility.Collapsed;
            UpdateBackButton();
        }

        private void UpdateBackButton()
        {
            var nav = SystemNavigationManager.GetForCurrentView();
            bool canGoBack = _activeView == View.Chat || _activeView == View.RoomInfo || _activeView == View.Settings || _activeView == View.AddRoom || _activeView == View.Invite;
            nav.AppViewBackButtonVisibility = canGoBack
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Collapsed;
        }

        private async void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            // The call overlay sits on top of everything, so Back ends the call first.
            if (CallOverlay != null && CallOverlay.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                HideCallOverlay();
                if (_callService != null) await _callService.HangupAsync();
                return;
            }

            // The full-screen image viewer overlays any view, so it gets first dibs on Back.
            if (ImageViewerPanel.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                CloseImageViewer();
                return;
            }

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
                case View.AddRoom:
                    e.Handled = true;
                    ShowView(View.RoomList);
                    break;
                case View.Invite:
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
                    existing.IsDirect = incoming.IsDirect;
                    existing.IsInvite = incoming.IsInvite;
                    existing.Inviter = incoming.Inviter;
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
            if (room == null) return;
            if (room.IsInvite)
            {
                ShowInvitePanel(room);
                return;
            }
            OpenRoom(room);
        }

        // The invite currently shown on the Invite panel (so the Accept/Decline buttons know which
        // room they act on).
        private Room _inviteRoom;

        /// <summary>
        /// Shows the Element-style invitation screen for a pending invite: the inviter's avatar,
        /// who they are, and Start chatting / Decline actions. Replaces the old MessageDialog (which
        /// silently failed on Windows 10 Mobile because it can't show more than two commands).
        /// </summary>
        private void ShowInvitePanel(Room room)
        {
            _inviteRoom = room;

            InviteTitle.Text = room.DisplayName;
            InviteSubtitle.Text = room.IsDirect ? "wants to chat" : "invited you to join this room";
            InviteUserId.Text = !string.IsNullOrEmpty(room.Inviter) ? room.Inviter : room.Id;

            // Avatar: reuse the resolved URL if the room list already fetched it, otherwise show the
            // colored initial and kick off a background fetch from the mxc.
            InviteAvatarInitial.Text = room.AvatarInitial;
            InviteAvatarFallback.Fill = room.AvatarBrush;
            ApplyInviteAvatar(room);

            InviteStatus.Visibility = Visibility.Collapsed;
            InviteStatus.Text = "";
            InviteProgress.IsActive = false;
            InviteAcceptButton.IsEnabled = true;
            InviteDeclineButton.IsEnabled = true;

            ShowView(View.Invite);

            // Resolve the avatar in the background when it isn't ready yet. If the room has no
            // avatar mxc at all (common for DM invites whose stripped state omits it) we still try,
            // because LoadInviteAvatarAsync falls back to the inviter's global profile avatar.
            if (!room.HasAvatar && (!string.IsNullOrEmpty(room.AvatarMxc) || !string.IsNullOrEmpty(room.Inviter)))
            {
                var _ = LoadInviteAvatarAsync(room);
            }
        }

        private void ApplyInviteAvatar(Room room)
        {
            if (room.HasAvatar)
            {
                InviteAvatarImage.Fill = new ImageBrush
                {
                    ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(room.AvatarUrl)),
                    Stretch = Stretch.UniformToFill
                };
                InviteAvatarImage.Visibility = Visibility.Visible;
                InviteAvatarInitial.Visibility = Visibility.Collapsed;
            }
            else
            {
                InviteAvatarImage.Visibility = Visibility.Collapsed;
                InviteAvatarInitial.Visibility = Visibility.Visible;
            }
        }

        private async Task LoadInviteAvatarAsync(Room room)
        {
            try
            {
                // The stripped invite_state often omits the inviter's avatar, so if we have no mxc
                // fall back to their global profile avatar (this is what Element shows).
                string mxc = room.AvatarMxc;
                if (string.IsNullOrEmpty(mxc) && !string.IsNullOrEmpty(room.Inviter))
                {
                    mxc = await _client.GetProfileAvatarAsync(room.Inviter);
                    if (!string.IsNullOrEmpty(mxc)) room.AvatarMxc = mxc;
                }
                if (string.IsNullOrEmpty(mxc)) return;

                string uri = await _media.GetThumbnailUriAsync(mxc, AvatarThumbSize);
                if (!string.IsNullOrEmpty(uri))
                {
                    room.AvatarUrl = uri;
                    // Only update the UI if this invite is still the one on screen.
                    if (_inviteRoom == room && _activeView == View.Invite)
                        ApplyInviteAvatar(room);
                }
            }
            catch (Exception ex)
            {
                App.Log("Invite avatar EXC '" + room.DisplayName + "': " + ex.Message);
            }
        }

        private void InviteCloseButton_Click(object sender, RoutedEventArgs e) => ShowView(View.RoomList);

        private async void InviteAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            var room = _inviteRoom;
            if (room == null) return;

            InviteProgress.IsActive = true;
            InviteAcceptButton.IsEnabled = false;
            InviteDeclineButton.IsEnabled = false;
            InviteStatus.Visibility = Visibility.Collapsed;
            try
            {
                await _client.JoinRoomAsync(room.Id);
                // Reflect the join locally right away; the next /sync delivers the timeline.
                _db.SetRoomInvite(room.Id, false);
                room.IsInvite = false;
                RefreshRooms();
                OpenRoom(room);
            }
            catch (Exception ex)
            {
                App.Log("Invite accept failed: " + ex.Message);
                InviteStatus.Text = "Could not join: " + ex.Message;
                InviteStatus.Visibility = Visibility.Visible;
                InviteAcceptButton.IsEnabled = true;
                InviteDeclineButton.IsEnabled = true;
            }
            finally
            {
                InviteProgress.IsActive = false;
            }
        }

        private async void InviteDeclineButton_Click(object sender, RoutedEventArgs e)
        {
            var room = _inviteRoom;
            if (room == null) return;

            InviteProgress.IsActive = true;
            InviteAcceptButton.IsEnabled = false;
            InviteDeclineButton.IsEnabled = false;
            try { await _client.LeaveRoomAsync(room.Id); }
            catch (Exception ex) { App.Log("Invite decline failed: " + ex.Message); }
            _db.DeleteRoom(room.Id);
            _inviteRoom = null;
            RefreshRooms();
            InviteProgress.IsActive = false;
            ShowView(View.RoomList);
        }


        /// <summary>
        /// Opens a room with a short entrance animation. Used when jumping straight into a freshly
        /// created direct chat so the transition from the Add Room panel feels intentional rather
        /// than an instant cut. OpenRoom runs ShowView(Chat) synchronously before its first await,
        /// so the chat view is already visible when we start the storyboard.
        /// </summary>
        private void OpenRoomAnimated(Room room)
        {
            OpenRoom(room);
            try
            {
                ChatView.Opacity = 0;
                ChatEnter.Begin();
            }
            catch { }
        }

        // ---- Chat ----

        private async void OpenRoom(Room room)
        {
            _openWatch.Restart();
            // Suppress the ListView entrance cascade for the bulk page load (re-enabled once the
            // open settles, see ScrollToBottomPass) so the room opens fully populated instead of
            // visibly animating items in one by one.
            SetMessageTransitions(false);
            _currentRoomId = room.Id;
            ChatRoomName.Text = room.DisplayName;
            ChatRoomMembers.Text = room.MemberText;

            // Clear unread locally and tell the server we've read the room (a read receipt resets
            // the server-side notification_count, so the badge doesn't reappear on the next sync).
            room.UnreadCount = 0;
            MarkRoomReadAsync(room.Id);

            ShowView(View.Chat);
            App.Log("PERF open: ShowView done @" + _openWatch.ElapsedMilliseconds + "ms");
            await LoadMessagesAsync(room.Id);
            App.Log("PERF open: LoadMessagesAsync done @" + _openWatch.ElapsedMilliseconds + "ms");
            ScrollMessagesToBottom();
            App.Log("PERF open: ScrollMessagesToBottom queued @" + _openWatch.ElapsedMilliseconds + "ms");
        }

        private async Task LoadMessagesAsync(string roomId)
        {
            Messages.Clear();

            // The day window. sinceTs only governs how far back the server backfill pulls into the
            // DB; the on-screen list itself is paged from the cache (latest page first), so memory
            // stays bounded regardless of how much history a room has.
            long sinceTs = DateTimeOffset.UtcNow.AddDays(-_settings.HistoryDays).ToUnixTimeMilliseconds();

            // ---- DIAGNOSTICS (room open) ----
            LogRoomDiag("OPEN", roomId, sinceTs);

            // Render the most recent page from cache immediately so the room opens instantly.
            RenderLatestPage(roomId);

            // The /sync filter only caches a handful of recent messages per room, so the
            // cache rarely covers the requested window on first open. Backfill from the
            // server when needed (into the DB), then re-render the latest page. This is what
            // makes the day window and "unlimited" actually have older history to page through.
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
                    RenderLatestPage(roomId);
                    // Re-render reset the list, so return to the latest message.
                    ScrollMessagesToBottom();
                }
            }
        }

        /// <summary>
        /// Renders the most recent <see cref="MessagePageSize"/> messages from the cache. Older
        /// messages are pulled in on demand by <see cref="LoadOlderMessagesAsync"/> when the user
        /// scrolls to the top, so only a small window is ever held in memory.
        /// </summary>
        private void RenderLatestPage(string roomId)
        {
            long tFetch0 = _openWatch.ElapsedMilliseconds;
            var names = _db.GetMemberNames(roomId);
            var msgs = _db.GetMessages(roomId, MessagePageSize);
            long tFetch1 = _openWatch.ElapsedMilliseconds;

            Messages.Clear();
            Message prev = null;
            foreach (var m in msgs)
            {
                DecorateMessage(m, names);
                SetDateSeparator(m, prev);
                prev = m;
                Messages.Add(m);
            }
            long tAdd1 = _openWatch.ElapsedMilliseconds;
            int imageCount = msgs.Count(x => x.IsImage && !string.IsNullOrEmpty(x.Mxc));
            App.Log("PERF render: count=" + msgs.Count + " images=" + imageCount
                + " dbFetchMs=" + (tFetch1 - tFetch0)
                + " addLoopMs=" + (tAdd1 - tFetch1)
                + " @" + tAdd1 + "ms");

            // There may be older messages to page in if we filled a whole page from cache, OR if
            // the room hasn't been fully downloaded yet (a freshly-joined busy room only has the
            // handful of messages /sync delivered). In the latter case scrolling up fetches the
            // rest from the server on demand — see LoadOlderMessagesAsync.
            _hasMoreOlder = msgs.Count >= MessagePageSize || !IsBackfillDone(roomId);
            _loadingOlder = false;

            // Resolve image thumbnails asynchronously (fire and forget).
            foreach (var m in msgs.Where(x => x.IsImage && !string.IsNullOrEmpty(x.Mxc)))
            {
                var _ = ResolveMessageImageAsync(m);
            }
        }

        /// <summary>
        /// Prepends the next older page of cached messages when the user scrolls to the top,
        /// preserving their reading position. Keeps the in-memory list growing only as far as
        /// the user actually scrolls back, instead of loading the whole room up front.
        /// </summary>
        private async Task LoadOlderMessagesAsync()
        {
            if (_loadingOlder || !_hasMoreOlder) return;
            if (Messages.Count == 0) return;

            string roomId = _currentRoomId;
            if (roomId == null) return;

            // Cursor = oldest message currently in memory.
            var anchor = Messages[0];
            _loadingOlder = true;
            try
            {
                var older = _db.GetMessagesBefore(roomId, anchor.Timestamp, anchor.EventId, MessagePageSize);

                App.Log("SCROLLUP room=" + roomId
                    + " anchorTs=" + DateTimeOffset.FromUnixTimeMilliseconds(anchor.Timestamp).LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                    + " cacheOlder=" + older.Count
                    + " bf_done=" + (IsBackfillDone(roomId) ? "1" : "0"));

                // Cache exhausted at the top -> pull older history from the server on demand. We do
                // this whenever the cache runs out, even if a previous run flagged the room "done":
                // that flag can be stale or wrong (e.g. an earlier backfill dead-ended on a bad
                // pagination token), so the server response is the source of truth. Without this, a
                // freshly-joined busy room (where /sync only delivered today's few messages) could
                // never show its history.
                if (older.Count == 0)
                {
                    // If the room was marked fully-fetched but the user clearly wants more, the flag
                    // is suspect. Re-anchor to the room's latest prev_batch (a known-good token) and
                    // clear the flag so we resume from a valid position rather than a dead-end token.
                    // Paging back from there re-covers recent messages (deduped) and reaches the gap.
                    if (IsBackfillDone(roomId))
                    {
                        string pb = _db.GetMeta(PrevBatchKey(roomId));
                        if (string.IsNullOrEmpty(pb))
                        {
                            // No anchor available and already done -> genuinely nothing more.
                            _hasMoreOlder = false;
                            return;
                        }
                        _db.SetMeta(BackfillTokenKey(roomId), pb);
                        _db.SetMeta(BackfillDoneKey(roomId), "0");
                    }

                    ShowSyncProgress(true, "Loading older messages…");
                    try
                    {
                        // sinceTs 0 = ignore the day window (the user is explicitly scrolling past
                        // it). A modest page budget keeps each scroll-up to a bounded fetch; busy
                        // rooms have long runs of non-message events, so allow several pages.
                        await TryBackfillAsync(roomId, 0, _syncCts?.Token ?? CancellationToken.None, maxPages: 8);
                    }
                    finally
                    {
                        ShowSyncProgress(false);
                    }

                    if (_currentRoomId != roomId) return; // navigated away while fetching
                    older = _db.GetMessagesBefore(roomId, anchor.Timestamp, anchor.EventId, MessagePageSize);
                }

                if (older.Count == 0)
                {
                    // Still nothing older after fetching. If the server reported the start of the
                    // room (done flag set during the fetch), stop. Otherwise keep the trigger armed
                    // so a further scroll-up pulls the next chunk (the page we fetched may have been
                    // all membership/state churn with no displayable messages).
                    _hasMoreOlder = !IsBackfillDone(roomId);
                    return;
                }

                var names = _db.GetMemberNames(roomId);
                Message prev = null;
                foreach (var m in older)
                {
                    DecorateMessage(m, names);
                    SetDateSeparator(m, prev);
                    prev = m;
                }
                // The previously-top message may no longer start its day now that an older page
                // sits above it; re-evaluate that single boundary.
                SetDateSeparator(anchor, older[older.Count - 1]);

                // Preserve scroll position: capture the extent before insert, then add the same
                // height back to the offset so the viewport stays on the same content.
                double prevExtent = _messagesScrollViewer != null ? _messagesScrollViewer.ExtentHeight : 0;
                double prevOffset = _messagesScrollViewer != null ? _messagesScrollViewer.VerticalOffset : 0;

                for (int i = older.Count - 1; i >= 0; i--)
                {
                    Messages.Insert(0, older[i]);
                }

                _hasMoreOlder = older.Count >= MessagePageSize || !IsBackfillDone(roomId);

                foreach (var m in older.Where(x => x.IsImage && !string.IsNullOrEmpty(x.Mxc)))
                {
                    var _ = ResolveMessageImageAsync(m);
                }

                if (_messagesScrollViewer != null)
                {
                    MessagesList.UpdateLayout();
                    double newExtent = _messagesScrollViewer.ExtentHeight;
                    _messagesScrollViewer.ChangeView(null, prevOffset + (newExtent - prevExtent), null, true);
                }
            }
            finally
            {
                _loadingOlder = false;
            }
        }

        private bool IsBackfillDone(string roomId)
        {
            return _db.GetMeta(BackfillDoneKey(roomId)) == "1";
        }

        /// <summary>
        /// Dumps the cache/backfill state for a room to the debug log so history-loading problems
        /// can be diagnosed from a saved log. Format is compact key=value so it's easy to read.
        /// </summary>
        private void LogRoomDiag(string tag, string roomId, long sinceTs)
        {
            try
            {
                int count = _db.CountMessages(roomId);
                long oldest = _db.GetOldestMessageTs(roomId);
                long newest = _db.GetNewestMessageTs(roomId);
                bool done = IsBackfillDone(roomId);
                string bfTok = _db.GetMeta(BackfillTokenKey(roomId));
                string pbTok = _db.GetMeta(PrevBatchKey(roomId));

                string Fmt(long ts) => ts <= 0 ? "-" :
                    DateTimeOffset.FromUnixTimeMilliseconds(ts).LocalDateTime.ToString("yyyy-MM-dd HH:mm");

                App.Log(tag + " room=" + roomId
                    + " stored=" + count
                    + " oldest=" + Fmt(oldest)
                    + " newest=" + Fmt(newest)
                    + " window>=" + Fmt(sinceTs) + " (" + _settings.HistoryDays + "d)"
                    + " bf_done=" + (done ? "1" : "0")
                    + " needsBackfill=" + NeedsBackfill(roomId, sinceTs)
                    + " bf_token=" + (string.IsNullOrEmpty(bfTok) ? "-" : "set")
                    + " prev_batch=" + (string.IsNullOrEmpty(pbTok) ? "MISSING" : "set"));
            }
            catch (Exception ex) { App.Log("LogRoomDiag EXC: " + ex.Message); }
        }

        /// <summary>
        /// Sets <see cref="Message.ShowDateSeparator"/> so a Telegram-style date pill appears at
        /// the start of each calendar day. <paramref name="previous"/> is the message immediately
        /// above (null when this is the very first).
        /// </summary>
        private static void SetDateSeparator(Message m, Message previous)
        {
            var day = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).LocalDateTime.Date;
            if (previous == null)
            {
                m.ShowDateSeparator = true;
                return;
            }
            var prevDay = DateTimeOffset.FromUnixTimeMilliseconds(previous.Timestamp).LocalDateTime.Date;
            m.ShowDateSeparator = day != prevDay;
        }

        private void MessagesList_Loaded(object sender, RoutedEventArgs e)
        {
            EnsureMessagesScrollViewer();
        }

        /// <summary>
        /// Finds the ListView's inner ScrollViewer and subscribes the scroll-up trigger, if not
        /// already done. Robust against virtualization timing: the ScrollViewer template part is
        /// often NOT realized when MessagesList_Loaded first fires, so a single attempt there can
        /// silently fail and leave scroll-up dead forever. We therefore also call this from the
        /// room-open path (after layout), so the wiring always gets established.
        /// </summary>
        private void EnsureMessagesScrollViewer()
        {
            if (_messagesScrollViewer != null) return;
            var sv = FindDescendant<ScrollViewer>(MessagesList);
            if (sv == null)
            {
                App.Log("ScrollViewer NOT found yet (will retry on open)");
                return;
            }
            _messagesScrollViewer = sv;
            _messagesScrollViewer.ViewChanged += MessagesScrollViewer_ViewChanged;
            App.Log("ScrollViewer wired up for scroll-up trigger");
        }

        private void MessagesScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            // Near the top -> pull in the next older page.
            if (_messagesScrollViewer != null && _messagesScrollViewer.VerticalOffset <= 60)
            {
                App.Log("SCROLL near-top offset=" + (int)_messagesScrollViewer.VerticalOffset
                    + " hasMoreOlder=" + _hasMoreOlder + " loadingOlder=" + _loadingOlder);
                if (_hasMoreOlder && !_loadingOlder)
                {
                    var _ = LoadOlderMessagesAsync();
                }
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = Windows.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Windows.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var deeper = FindDescendant<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        /// <summary>
        /// True when the cached history doesn't yet reach back to <paramref name="sinceTs"/>
        /// (or, for unlimited, to the start of the room). A per-room "done" flag stops us from
        /// re-paging a room whose history we've already fetched to the beginning.
        /// </summary>
        private bool NeedsBackfill(string roomId, long sinceTs)
        {
            if (IsBackfillDone(roomId)) return false;

            long oldest = _db.GetOldestMessageTs(roomId);
            if (oldest == 0) return true;            // nothing cached yet
            return oldest > sinceTs;                 // cache starts after the window -> fetch older
        }

        private static string BackfillDoneKey(string roomId) { return "bf_done_" + roomId; }
        private static string BackfillTokenKey(string roomId) { return "bf_token_" + roomId; }
        private static string PrevBatchKey(string roomId) { return "pb_" + roomId; }

        private void DecorateMessage(Message m, Dictionary<string, string> names)
        {
            m.IsMine = m.Sender == _settings.UserId;
            string display;
            if (m.Sender != null && names.TryGetValue(m.Sender, out display))
                m.SenderDisplay = display;
        }

        private async Task ResolveMessageImageAsync(Message m)
        {
            long t0 = _openWatch.IsRunning ? _openWatch.ElapsedMilliseconds : -1;
            try
            {
                string uri = await _media.GetThumbnailUriAsync(m.Mxc, ImageThumbSize);
                if (uri != null) m.MediaUrl = uri;
                if (t0 >= 0)
                    App.Log("PERF image: resolved in " + (_openWatch.ElapsedMilliseconds - t0)
                        + "ms (done @" + _openWatch.ElapsedMilliseconds + "ms)");
            }
            catch { }
        }

        private async Task<int> TryBackfillAsync(string roomId, long sinceTs, CancellationToken ct, Action<int> onPageStored = null, int maxPages = 200)
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
            int fetched = 0;

            // Never backfill the same room from two places at once (e.g. the user opening a
            // room while the background loop is already paging it) — they'd share the token.
            lock (_bfLock) { if (!_backfillInFlight.Add(roomId)) return 0; }
            try
            {
                // Resume from where a previous backfill left off, if any. On the very first
                // backfill for a room there's no saved token, so anchor on the room's prev_batch
                // (captured from /sync) — the correct, server-valid starting point for paging
                // history backward. Falling back to an empty token made matrix.org's pagination
                // unreliable for busy rooms (it could return a dead-end token after only the most
                // recent page, leaving 59 of 60 days unreachable).
                string from = _db.GetMeta(BackfillTokenKey(roomId));
                if (string.IsNullOrEmpty(from))
                    from = _db.GetMeta(PrevBatchKey(roomId));

                App.Log("BF start room=" + roomId + " sinceTs=" + sinceTs + " maxPages=" + maxPages
                    + " anchor=" + (string.IsNullOrEmpty(from) ? "EMPTY(no token/no prev_batch)"
                        : (_db.GetMeta(BackfillTokenKey(roomId)) != null ? "bf_token" : "prev_batch")));

                for (int page = 0; page < maxPages; page++)
                {
                    if (ct.IsCancellationRequested) break;

                    var resp = await _client.GetRoomMessagesAsync(roomId, pageSize, from, ct).ConfigureAwait(false);
                    var chunk = resp != null && resp.ContainsKey("chunk") ? resp.GetNamedArray("chunk") : null;
                    if (chunk == null || chunk.Count == 0)
                    {
                        // Reached the start of the room's history.
                        _db.SetMeta(BackfillDoneKey(roomId), "1");
                        App.Log("BF page=" + page + " EMPTY chunk -> bf_done set (room start)");
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

                    App.Log("BF page=" + page + " events=" + chunk.Count + " stored=" + pageStored
                        + " oldestInPage=" + (oldestTs == long.MaxValue ? "-"
                            : DateTimeOffset.FromUnixTimeMilliseconds(oldestTs).LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
                        + " end=" + (string.IsNullOrEmpty(end) ? "EMPTY" : (end == from ? "SAME-AS-FROM" : "next")));

                    if (string.IsNullOrEmpty(end) || end == from)
                    {
                        // No further pages available -> we've reached the start.
                        _db.SetMeta(BackfillDoneKey(roomId), "1");
                        App.Log("BF page=" + page + " end empty/same -> bf_done set");
                        break;
                    }
                    from = end;
                    _db.SetMeta(BackfillTokenKey(roomId), from);

                    // Stop once we've paged past the requested window (unless unlimited).
                    if (sinceTs > 0 && oldestTs != long.MaxValue && oldestTs < sinceTs)
                    {
                        App.Log("BF page=" + page + " crossed window (oldestInPage < sinceTs) -> stop");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { /* suspended/cancelled: resume next time. */ }
            catch { /* Offline or error: show whatever is cached; resume next time. */ }
            finally
            {
                lock (_bfLock) { _backfillInFlight.Remove(roomId); }
            }
            App.Log("BF done room=" + roomId + " fetched=" + fetched);
            return fetched;
        }

        /// <summary>
        /// Folds a /sync update into the currently-open room WITHOUT rebuilding the whole list.
        /// A full Messages.Clear()+reload threw away the user's scroll position on every sync,
        /// which (combined with variable-height items being re-measured) made a just-sent message
        /// jump and then get shoved up a line or two. Instead we drop confirmed local echoes and
        /// append only genuinely new messages, and only auto-scroll if the user was already at the
        /// bottom — so reading older history is never interrupted either.
        /// </summary>
        private void RefreshCurrentRoomMessages()
        {
            string roomId = _currentRoomId;
            if (roomId == null) return;

            bool atBottom = IsScrolledToBottom();

            var names = _db.GetMemberNames(roomId);
            var latest = _db.GetMessages(roomId, MessagePageSize);

            var latestIds = new HashSet<string>();
            foreach (var m in latest) latestIds.Add(m.EventId);

            var shownIds = new HashSet<string>();
            foreach (var m in Messages) shownIds.Add(m.EventId);

            // 1. Reconcile confirmed local echoes IN PLACE. When the server echoes our just-sent
            //    message back via /sync, SyncProcessor deletes the echo row and inserts the real
            //    event (with a different event id). If we removed the on-screen echo and added the
            //    real message as a new item, the ListView would play its entrance transition a
            //    SECOND time (the user sees the bubble fade in twice). Instead we find the matching
            //    confirmed message and mutate the existing echo object: same instance stays in the
            //    collection, so no remove/add and no extra animation. Content is identical (same
            //    body), so nothing visibly changes.
            var consumed = new HashSet<string>();
            for (int i = 0; i < Messages.Count; i++)
            {
                var echo = Messages[i];
                if (!echo.IsLocalEcho || latestIds.Contains(echo.EventId)) continue;

                Message match = null;
                foreach (var c in latest)
                {
                    if (shownIds.Contains(c.EventId) || consumed.Contains(c.EventId)) continue;
                    if (c.Sender == echo.Sender && c.MsgType == echo.MsgType && c.Body == echo.Body)
                    {
                        match = c;
                        break;
                    }
                }

                if (match != null)
                {
                    // Promote the echo to the confirmed event without re-adding it.
                    echo.EventId = match.EventId;
                    echo.IsLocalEcho = false;
                    consumed.Add(match.EventId);
                    shownIds.Add(match.EventId);
                }
            }

            // 2. Remove any local echoes that were NOT confirmed (e.g. failed sends already marked,
            //    or echoes with no matching server event in this page).
            for (int i = Messages.Count - 1; i >= 0; i--)
            {
                if (Messages[i].IsLocalEcho && !latestIds.Contains(Messages[i].EventId))
                    Messages.RemoveAt(i);
            }

            // 3. Append only messages newer than the last one on screen. Older history is paged in
            //    separately via scroll-back, so we never insert into the middle here.
            Message prev = Messages.Count > 0 ? Messages[Messages.Count - 1] : null;
            foreach (var m in latest)
            {
                if (shownIds.Contains(m.EventId) || consumed.Contains(m.EventId)) continue;
                if (prev != null &&
                    (m.Timestamp < prev.Timestamp ||
                     (m.Timestamp == prev.Timestamp && string.CompareOrdinal(m.EventId, prev.EventId) <= 0)))
                    continue;

                DecorateMessage(m, names);
                SetDateSeparator(m, prev);
                prev = m;
                Messages.Add(m);

                if (m.IsImage && !string.IsNullOrEmpty(m.Mxc))
                {
                    var _ = ResolveMessageImageAsync(m);
                }
            }

            // Follow new content only if the user hadn't scrolled up to read history.
            if (atBottom) ScrollMessagesToBottom();
        }

        /// <summary>
        /// Marks a room as read: clears the local unread badge (DB + bound Room) and sends an
        /// m.read receipt up to the newest event so the homeserver stops counting it as unread.
        /// Without the receipt the server keeps reporting notification_count &gt; 0 and the badge
        /// reappears on the next /sync even though the user has already read the room.
        /// </summary>
        private async void MarkRoomReadAsync(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return;

            // Local clear first (synchronous, so the badge updates immediately and a following
            // RefreshRooms reads 0 from the DB instead of the server's stale count).
            _db.SetRoomUnread(roomId, 0);
            foreach (var r in Rooms)
            {
                if (r.Id == roomId) { r.UnreadCount = 0; break; }
            }

            // Tell the server (best effort: a failed receipt must never disrupt the UI).
            try
            {
                string eventId = _db.GetLatestRealEventId(roomId);
                if (!string.IsNullOrEmpty(eventId))
                    await _client.SendReadReceiptAsync(roomId, eventId);
            }
            catch (Exception ex)
            {
                App.Log("Read receipt failed (" + roomId + "): " + ex.Message);
            }
        }

        /// <summary>True when the message list is at (or within a small threshold of) the bottom.</summary>
        private bool IsScrolledToBottom()
        {
            if (_messagesScrollViewer == null) return true; // not yet measured -> treat as bottom
            return _messagesScrollViewer.VerticalOffset >= _messagesScrollViewer.ScrollableHeight - 80;
        }

        /// <summary>
        /// Toggles the message list's item transitions. Disabled during a room's bulk page load so
        /// all messages appear at once (no entrance "cascade"); enabled afterwards so only live
        /// messages arriving later animate in.
        /// </summary>
        private void SetMessageTransitions(bool enabled)
        {
            if (MessagesList == null) return;
            if (enabled)
            {
                if (MessagesList.ItemContainerTransitions == null || MessagesList.ItemContainerTransitions.Count == 0)
                    MessagesList.ItemContainerTransitions = new Windows.UI.Xaml.Media.Animation.TransitionCollection
                    {
                        new Windows.UI.Xaml.Media.Animation.AddDeleteThemeTransition()
                    };
            }
            else
            {
                MessagesList.ItemContainerTransitions = new Windows.UI.Xaml.Media.Animation.TransitionCollection();
            }
        }

        private void ScrollMessagesToBottom()
        {
            if (Messages.Count == 0) return;

            // With ItemsUpdatingScrollMode="KeepLastItemInView" the framework keeps the newest item
            // pinned as items are added/resized. For an explicit jump (room open, after sending) we
            // still nudge it: ScrollIntoView realizes and aligns the last item, then ChangeView pins
            // to the very bottom. We repeat across a few frames so async image decode (which grows
            // the last bubbles after we first scroll) is caught without reading the racy, lagging
            // VerticalOffset.
            ScrollToBottomPass(Messages[Messages.Count - 1], 6);
        }

        private void ScrollToBottomPass(Message last, int attemptsLeft)
        {
            if (attemptsLeft <= 0 || last == null) return;

            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try
                {
                    // The list is laid out by now, so this is a reliable place to (re)wire the
                    // scroll-up trigger if MessagesList_Loaded fired before the ScrollViewer existed.
                    EnsureMessagesScrollViewer();

                    MessagesList.ScrollIntoView(last);
                    if (_messagesScrollViewer != null)
                    {
                        _messagesScrollViewer.UpdateLayout();
                        App.Log("PERF scrollpass: extent=" + (int)_messagesScrollViewer.ExtentHeight
                            + " scrollable=" + (int)_messagesScrollViewer.ScrollableHeight
                            + " offset=" + (int)_messagesScrollViewer.VerticalOffset
                            + " @" + _openWatch.ElapsedMilliseconds + "ms");
                        _messagesScrollViewer.ChangeView(null, _messagesScrollViewer.ScrollableHeight, null, true);
                    }
                }
                catch { }

                // Note: we intentionally do NOT re-enable item transitions for live messages.
                // The entrance/reposition animation made incoming messages (especially several at
                // once) look like the whole list was redrawing. Keeping transitions off makes a new
                // message simply appear at the end, which is the expected chat behavior.
                ScrollToBottomPass(last, attemptsLeft - 1);
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
            SetEchoDateSeparator(echo);
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

        private async void LeaveRoomButton_Click(object sender, RoutedEventArgs e)
        {
            string roomId = _currentRoomId;
            if (roomId == null) return;

            var room = Rooms.FirstOrDefault(r => r.Id == roomId);
            string name = room != null ? room.DisplayName : "this room";

            var confirm = new Windows.UI.Popups.MessageDialog(
                "Leave \"" + name + "\"? It will be removed from your room list.",
                "Leave room");
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Leave"));
            confirm.Commands.Add(new Windows.UI.Popups.UICommand("Cancel"));
            confirm.DefaultCommandIndex = 1;
            confirm.CancelCommandIndex = 1;

            var choice = await confirm.ShowAsync();
            if (choice.Label != "Leave") return;

            LeaveRoomButton.IsEnabled = false;
            try
            {
                await _client.LeaveRoomAsync(roomId);

                // Drop the local cache for the room and update the UI.
                _db.DeleteRoom(roomId);
                if (room != null) Rooms.Remove(room);
                _currentRoomId = null;
                Messages.Clear();
                ShowView(View.RoomList);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not leave room: " + ex.Message);
            }
            finally
            {
                LeaveRoomButton.IsEnabled = true;
            }
        }

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

                    // Deliver any WebRTC call signalling events to the CallService. We're already
                    // on the UI thread here (the await resumed on it), which is required because the
                    // WebRTC event queue is bound to this thread.
                    if (_callService != null && result.CallSignals.Count > 0)
                    {
                        foreach (var sig in result.CallSignals)
                        {
                            try { await _callService.HandleSignalAsync(sig); }
                            catch (Exception ex) { App.Log("CALL: signal dispatch failed: " + ex.Message); }
                        }
                    }

                    if (result.HasChanges)
                    {
                        // If new events landed in the room the user is currently viewing, they are
                        // effectively read: clear the unread (and send a read receipt) BEFORE
                        // RefreshRooms so it doesn't repaint a stale badge for the open room.
                        if (_activeView == View.Chat && _currentRoomId != null &&
                            result.ChangedRooms.Contains(_currentRoomId))
                            MarkRoomReadAsync(_currentRoomId);

                        if (_activeView == View.RoomList || _activeView == View.Chat)
                            RefreshRooms();
                        if (_currentRoomId != null && result.ChangedRooms.Contains(_currentRoomId))
                            RefreshCurrentRoomMessages();
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
                        RenderLatestPage(roomId);
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
            return DateTimeOffset.UtcNow.AddDays(-_settings.HistoryDays).ToUnixTimeMilliseconds();
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

        /// <summary>
        /// Saves the full diagnostic log to a user-chosen location with a timestamped name like
        /// 20260623-164612-debug.log. Prefers the complete on-disk startup.log (which has the full
        /// history, not just the truncated on-screen buffer); falls back to the overlay buffer.
        /// </summary>
        private async void DebugSave_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string contents = null;
                try
                {
                    var local = Windows.Storage.ApplicationData.Current.LocalFolder;
                    var logFile = await local.GetFileAsync("startup.log");
                    contents = await Windows.Storage.FileIO.ReadTextAsync(logFile);
                }
                catch { /* no startup.log yet -> use the on-screen buffer below */ }

                if (string.IsNullOrEmpty(contents)) contents = _debugBuffer.ToString();

                string name = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-debug.log";

                var picker = new Windows.Storage.Pickers.FileSavePicker
                {
                    SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                    SuggestedFileName = name
                };
                picker.FileTypeChoices.Add("Log file", new List<string> { ".log" });

                var file = await picker.PickSaveFileAsync();
                if (file == null) return; // user cancelled

                await Windows.Storage.FileIO.WriteTextAsync(file, contents);
                App.Log("Debug log saved to " + file.Path);
            }
            catch (Exception ex)
            {
                App.Log("DebugSave EXC: " + ex.Message);
            }
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
