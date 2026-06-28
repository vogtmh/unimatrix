using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using UniMatrix.Services;

namespace UniMatrix
{
    /// <summary>
    /// MatrixRTC (Element Call / Matrix 2.0) incoming-call awareness. Element no longer rings 1:1
    /// calls over the legacy m.call.* protocol — it sends an MSC4075 ring (m.rtc.notification) and
    /// joins a LiveKit SFU. UniMatrix's WebRTC stack (Org.WebRtc M71) can't join that SFU, so this
    /// layer does NOT attempt media: it rings the phone, shows the incoming-call screen in a
    /// read-only "answer in Element" mode, drops a timeline tile, and auto-dismisses when the call
    /// ends (the caller's m.rtc.member membership goes empty) or the ring lifetime expires.
    ///
    /// The legacy peer-to-peer CallService (UniMatrix &lt;-&gt; UniMatrix calls) is untouched and keeps
    /// working; this only adds handling for the Element/MatrixRTC ring it can't otherwise answer.
    /// </summary>
    public sealed partial class MainPage
    {
        // The room whose MatrixRTC ring is currently showing on the overlay, or null when not ringing.
        private string _matrixRtcRingingRoomId;

        // True while the call overlay is showing a MatrixRTC ring (so the accept/decline handlers and
        // HideCallOverlay know to use the MatrixRTC behaviour rather than the legacy path).
        private bool _incomingIsMatrixRtc;

        // LiveKit focus info for the currently-ringing call (from the caller's m.rtc.member), used to
        // obtain SFU credentials when the user accepts.
        private string _matrixRtcFocusUrl;
        private string _matrixRtcRoomAlias;

        // MatrixRTC -> LiveKit authorization (OpenID token -> SFU JWT). Lazily created.
        private LiveKitAuthService _liveKitAuth;

        // LiveKit signalling socket for the in-progress join (connect -> JoinResponse -> media).
        private LiveKitSignalClient _liveKitSignal;

        // WebRTC media half of the in-progress join (subscriber/publisher peer connections).
        private LiveKitMediaSession _liveKitMedia;

        // True while an accept/join attempt is in progress, so a double-tap doesn't fire twice.
        private bool _matrixRtcJoining;

        // Auto-dismisses the ring after the notification's lifetime so it never rings forever.
        private DispatcherTimer _matrixRtcRingTimer;

        // Notification event ids already handled, so a re-delivered ring (or a sync replay) doesn't
        // ring twice. Bounded to avoid unbounded growth over a long session.
        private readonly HashSet<string> _handledRtcNotifications = new HashSet<string>();

        // Active MatrixRTC call members per room (state-key set), so we can tell when a call ends.
        private readonly Dictionary<string, HashSet<string>> _rtcActiveMembers =
            new Dictionary<string, HashSet<string>>();

        // Rooms we've already rung for in the current active call session, so a periodic membership
        // refresh (Element re-publishes m.rtc.member while the call is up) doesn't ring repeatedly.
        // Cleared for a room when its call ends (active members drop to zero).
        private readonly HashSet<string> _rtcRangForRoom = new HashSet<string>();

        // Default ring lifetime when a notification doesn't specify one (MSC4075 suggests ~30s).
        private const long DefaultRtcRingLifetimeMs = 30000;

        /// <summary>
        /// Applies the MatrixRTC parts of a sync result on the UI thread: first the membership
        /// changes (which may end an active call and dismiss a stale ring), then any fresh ring
        /// notifications. Called from the sync loop after CallSignals are dispatched.
        /// </summary>
        private void DispatchMatrixRtc(SyncResult result)
        {
            if (result == null) return;

            if (result.MatrixRtcMemberships != null)
            {
                foreach (var m in result.MatrixRtcMemberships)
                {
                    try { UpdateRtcMembership(m); }
                    catch (Exception ex) { App.Log("RTC: membership update failed: " + ex.Message); }
                }
            }

            if (result.MatrixRtcNotifications != null)
            {
                foreach (var n in result.MatrixRtcNotifications)
                {
                    try { HandleMatrixRtcNotification(n); }
                    catch (Exception ex) { App.Log("RTC: notification handling failed: " + ex.Message); }
                }
            }
        }

        /// <summary>
        /// Tracks who is currently in a room's MatrixRTC call. A remote user joining is the actual
        /// incoming-call signal in practice — Element publishes an m.rtc.member join (and re-publishes
        /// it periodically) but does NOT always send a separate m.rtc.notification ring — so this is
        /// what drives the ring. When the room we're ringing for drops to zero active members, the
        /// caller has left, so the ring is dismissed.
        /// </summary>
        private void UpdateRtcMembership(MatrixRtcMembership m)
        {
            if (m == null || string.IsNullOrEmpty(m.RoomId) || string.IsNullOrEmpty(m.StateKey)) return;

            HashSet<string> set;
            if (!_rtcActiveMembers.TryGetValue(m.RoomId, out set))
            {
                set = new HashSet<string>();
                _rtcActiveMembers[m.RoomId] = set;
            }

            if (m.Active) set.Add(m.StateKey);
            else set.Remove(m.StateKey);

            App.Log("RTC: member " + (m.Sender ?? m.UserId ?? m.StateKey) + " " + (m.Active ? "joined" : "left") +
                    " call in " + m.RoomId + " (active=" + set.Count + ")");

            // The call has ended (everyone left): reset the "already rang" guard and, if we were
            // ringing for this room, dismiss the ring.
            if (set.Count == 0)
            {
                _rtcRangForRoom.Remove(m.RoomId);
                if (_incomingIsMatrixRtc && m.RoomId == _matrixRtcRingingRoomId)
                    DismissMatrixRtcRing("call ended");
                return;
            }

            // A remote user is now in the call -> ring (unless this is our own membership, we're
            // already in a legacy call, already ringing, or we've already rung for this call).
            if (!m.Active) return;
            bool isSelf = !string.IsNullOrEmpty(_settings?.UserId) &&
                          (m.Sender == _settings.UserId || (m.UserId != null && m.UserId.StartsWith(_settings.UserId)));
            if (isSelf) return;
            if (_rtcRangForRoom.Contains(m.RoomId)) return;
            if (_callService != null && _callService.InCall) return;
            if (_incomingIsMatrixRtc) return;

            // Ignore stale joins replayed from history/backfill (only ring for a recent join).
            if (m.Timestamp > 0)
            {
                long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - m.Timestamp;
                if (age > DefaultRtcRingLifetimeMs)
                {
                    App.Log("RTC: stale member join (" + age + "ms) -> not ringing");
                    return;
                }
            }

            _rtcRangForRoom.Add(m.RoomId);
            App.Log("RTC: ringing for MatrixRTC call (member join) in " + m.RoomId + " from " + (m.Sender ?? "?"));

            // Remember the LiveKit focus so an accept can request SFU credentials.
            _matrixRtcFocusUrl = m.FocusServiceUrl;
            _matrixRtcRoomAlias = !string.IsNullOrEmpty(m.FocusRoomAlias) ? m.FocusRoomAlias : m.RoomId;

            // Drop a timeline tile for the incoming call (best-effort).
            try
            {
                var tile = SyncProcessor.BuildRtcMissedTile(m.RoomId, m.RoomId, m.Sender, m.Timestamp, _settings?.UserId);
                if (tile != null) _db?.UpsertMessage(tile);
            }
            catch (Exception ex) { App.Log("RTC: tile upsert failed: " + ex.Message); }

            _matrixRtcRingingRoomId = m.RoomId;
            _incomingIsMatrixRtc = true;
            ShowMatrixRtcIncomingOverlay(m.RoomId);
            StartRingVibration();

            // Auto-dismiss after the default lifetime if no leave is ever seen.
            if (_matrixRtcRingTimer == null)
            {
                _matrixRtcRingTimer = new DispatcherTimer();
                _matrixRtcRingTimer.Tick += (s, e) => DismissMatrixRtcRing("ring timed out");
            }
            _matrixRtcRingTimer.Interval = TimeSpan.FromMilliseconds(DefaultRtcRingLifetimeMs);
            _matrixRtcRingTimer.Start();
        }

        /// <summary>
        /// Rings the phone for a fresh, relevant MSC4075 ring notification. Ignores our own device's
        /// notifications, stale/replayed ones, "notification" (non-ring) types, and rings that arrive
        /// while a legacy call is already in progress.
        /// </summary>
        private void HandleMatrixRtcNotification(MatrixRtcNotification n)
        {
            if (n == null || string.IsNullOrEmpty(n.RoomId)) return;

            // De-dupe: each notification event rings at most once.
            if (!string.IsNullOrEmpty(n.EventId))
            {
                if (_handledRtcNotifications.Contains(n.EventId)) return;
                if (_handledRtcNotifications.Count > 256) _handledRtcNotifications.Clear();
                _handledRtcNotifications.Add(n.EventId);
            }

            // Our own ring (another of our devices started the call): nothing to answer here.
            if (!string.IsNullOrEmpty(_settings?.UserId) && n.Sender == _settings.UserId) return;

            // Only "ring" notifications ring; "notification" is a silent presence hint.
            if (!string.IsNullOrEmpty(n.NotificationType) && n.NotificationType != "ring")
            {
                App.Log("RTC: notification type=" + n.NotificationType + " (not a ring) -> ignored");
                return;
            }

            // Drop stale rings (history backfill / sync replay): only ring within the lifetime window.
            long lifetime = n.Lifetime > 0 ? n.Lifetime : DefaultRtcRingLifetimeMs;
            long age = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - n.Timestamp;
            if (n.Timestamp > 0 && age > lifetime)
            {
                App.Log("RTC: stale ring (" + age + "ms > " + lifetime + "ms) -> not ringing");
                return;
            }

            // Don't hijack the screen if a legacy 1:1 call is already up, or we're already ringing.
            if (_callService != null && _callService.InCall) return;
            if (_incomingIsMatrixRtc) return;

            App.Log("RTC: ringing for MatrixRTC call in " + n.RoomId + " from " + (n.Sender ?? "?"));

            _matrixRtcRingingRoomId = n.RoomId;
            _incomingIsMatrixRtc = true;
            ShowMatrixRtcIncomingOverlay(n.RoomId);
            StartRingVibration();

            // Auto-dismiss when the ring's lifetime elapses (caller may never publish a leave we see).
            long remaining = n.Timestamp > 0 ? Math.Max(1000, lifetime - age) : lifetime;
            if (_matrixRtcRingTimer == null)
            {
                _matrixRtcRingTimer = new DispatcherTimer();
                _matrixRtcRingTimer.Tick += (s, e) => DismissMatrixRtcRing("ring timed out");
            }
            _matrixRtcRingTimer.Interval = TimeSpan.FromMilliseconds(remaining);
            _matrixRtcRingTimer.Start();
        }

        /// <summary>
        /// Shows the call overlay as a MatrixRTC incoming ring. The Accept button is shown labeled
        /// "Join" (UniMatrix attempts to join the LiveKit SFU); Decline becomes "Dismiss".
        /// </summary>
        private void ShowMatrixRtcIncomingOverlay(string roomId)
        {
            ShowCallOverlay(incoming: true, roomId: roomId,
                            peerName: GetRoomDisplayName(roomId),
                            status: "Incoming MatrixRTC call");

            if (CallAcceptButton != null) CallAcceptButton.Visibility = Visibility.Visible;
            if (CallAcceptLabel != null) CallAcceptLabel.Text = "Join";
            if (CallDeclineButton != null)
            {
                Grid.SetColumnSpan(CallDeclineButton, 1);
                CallDeclineButton.Margin = new Thickness(0, 0, 6, 0);
            }
            if (CallDeclineLabel != null) CallDeclineLabel.Text = "Dismiss";
        }

        /// <summary>
        /// User tapped "Join" on a MatrixRTC ring. Step 1 of joining: obtain LiveKit SFU credentials
        /// (Matrix OpenID token -> focus service -> {jwt, url}). Media join (LiveKit signalling +
        /// WebRTC) is the next phase; for now this proves the authorization path end to end and logs
        /// the result so we know matrix.org grants SFU access.
        /// </summary>
        internal async void AcceptMatrixRtcCall()
        {
            if (!_incomingIsMatrixRtc || _matrixRtcJoining) return;
            _matrixRtcJoining = true;

            // Stop the ring vibration/auto-dismiss while we attempt to join.
            StopRingVibration();
            if (_matrixRtcRingTimer != null) _matrixRtcRingTimer.Stop();

            string room = _matrixRtcRoomAlias;
            string focus = _matrixRtcFocusUrl;
            if (CallStatusText != null) CallStatusText.Text = "Connecting\u2026";

            try
            {
                if (string.IsNullOrEmpty(focus))
                {
                    App.Log("RTC: cannot join — no LiveKit focus url from caller");
                    if (CallStatusText != null) CallStatusText.Text = "Can't join (no SFU info)";
                    _matrixRtcJoining = false;
                    return;
                }

                var openId = await _client.RequestOpenIdTokenAsync();
                if (openId == null)
                {
                    if (CallStatusText != null) CallStatusText.Text = "Can't join (auth failed)";
                    _matrixRtcJoining = false;
                    return;
                }

                if (_liveKitAuth == null) _liveKitAuth = new LiveKitAuthService();
                var creds = await _liveKitAuth.GetSfuCredentialsAsync(
                    focus, room, openId, _settings?.DeviceId);

                if (creds == null)
                {
                    if (CallStatusText != null) CallStatusText.Text = "Can't join (SFU denied)";
                    _matrixRtcJoining = false;
                    return;
                }

                // Authorization succeeded. Open the LiveKit signalling socket and wait for the
                // server's JoinResponse (ICE servers, subscriber-primary mode, peers). Media wiring
                // (WebRTC peer connections) is layered on top of these signalling events next.
                App.Log("RTC: JOIN AUTH OK — SFU url=" + creds.Url);
                if (CallStatusText != null) CallStatusText.Text = "Connecting to call\u2026";

                StartLiveKitSignalling(creds.Url, creds.Jwt);
            }
            catch (Exception ex)
            {
                App.Log("RTC: join attempt failed: " + ex.Message);
                if (CallStatusText != null) CallStatusText.Text = "Join failed";
                _matrixRtcJoining = false;
            }
        }

        /// <summary>
        /// Opens the LiveKit signalling WebSocket with the SFU credentials and wires its events. On a
        /// JoinResponse we know the connection is live; receiving media (subscriber offer -> answer)
        /// and publishing the mic are the subsequent milestones.
        /// </summary>
        private void StartLiveKitSignalling(string sfuUrl, string jwt)
        {
            CloseLiveKitSignalling();

            var signal = new LiveKitSignalClient();
            _liveKitSignal = signal;

            // Wire the media session (subscriber/publisher peer connections) BEFORE connecting, so it
            // is subscribed to the signalling events when the SFU's JoinResponse + subscriber offer
            // arrive. It captures the ICE servers from the join and answers the subscriber offer.
            var media = new LiveKitMediaSession(Dispatcher, signal);
            _liveKitMedia = media;
            media.StatusChanged += s =>
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (_liveKitMedia != media) return;
                    App.Log("RTC: media " + s);
                });
            };

            signal.JoinReceived += join =>
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (_liveKitSignal != signal) return; // superseded
                    if (CallStatusText != null)
                        CallStatusText.Text = "In call \u00B7 " + (join.OtherParticipantCount + 1) + " on SFU";
                });
            };

            signal.Closed += reason =>
            {
                _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    if (_liveKitSignal != signal) return;
                    App.Log("RTC: signalling closed (" + reason + ")");
                    if (CallStatusText != null) CallStatusText.Text = "Call disconnected";
                });
            };

            _ = signal.ConnectAsync(sfuUrl, jwt);
        }

        /// <summary>Closes and clears the LiveKit signalling socket and media session if open.</summary>
        private void CloseLiveKitSignalling()
        {
            var media = _liveKitMedia;
            _liveKitMedia = null;
            if (media != null)
            {
                try { media.Close(); } catch { }
            }

            var signal = _liveKitSignal;
            _liveKitSignal = null;
            if (signal != null)
            {
                try { signal.Close("local hangup"); } catch { }
            }
        }

        /// <summary>Stops a MatrixRTC ring and restores the overlay to its normal (legacy-call) layout.</summary>
        private void DismissMatrixRtcRing(string reason)
        {
            if (!_incomingIsMatrixRtc) return;
            App.Log("RTC: dismissing ring (" + reason + ")");

            _incomingIsMatrixRtc = false;
            _matrixRtcJoining = false;
            _matrixRtcRingingRoomId = null;
            _matrixRtcFocusUrl = null;
            _matrixRtcRoomAlias = null;
            CloseLiveKitSignalling();
            if (_matrixRtcRingTimer != null) _matrixRtcRingTimer.Stop();
            StopRingVibration();
            ResetMatrixRtcOverlayChrome();
            HideCallOverlay();
        }

        /// <summary>Restores the accept/decline panel to its default two-button (Decline + Accept) state.</summary>
        private void ResetMatrixRtcOverlayChrome()
        {
            if (CallAcceptButton != null) CallAcceptButton.Visibility = Visibility.Visible;
            if (CallAcceptLabel != null) CallAcceptLabel.Text = "Accept";
            if (CallDeclineButton != null)
            {
                Grid.SetColumnSpan(CallDeclineButton, 1);
                CallDeclineButton.Margin = new Thickness(0, 0, 6, 0);
            }
            if (CallDeclineLabel != null) CallDeclineLabel.Text = "Decline";
        }
    }
}
