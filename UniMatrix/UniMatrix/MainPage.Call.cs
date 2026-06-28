using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using UniMatrix.Models;
using UniMatrix.Services;

namespace UniMatrix
{
    /// <summary>
    /// 1:1 audio call UI glue: the chat-header call button, the incoming-call accept/decline
    /// overlay and the active-call hang-up button, plus the CallService event handlers that
    /// drive the overlay. The actual WebRTC + Matrix signalling lives in
    /// <see cref="UniMatrix.Services.CallService"/>; this file only translates between that
    /// service and the on-screen panel.
    /// </summary>
    public sealed partial class MainPage
    {
        // Ticks once a second while a call is connected, updating CallTimerText.
        private DispatcherTimer _callTimer;
        private DateTimeOffset _callConnectedAt;

        // Pulses the vibration motor repeatedly while an incoming call is ringing.
        private DispatcherTimer _ringVibrateTimer;

        // ---- User actions ----

        private async void CallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoomId == null) return;
            if (_callService == null) return;

            if (!_callService.IsWebRtcAvailable)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "Audio calling needs the ARM build of UniMatrix running on the phone.",
                    "Calling unavailable").ShowAsync();
                return;
            }
            if (_callService.InCall) return;

            ShowCallOverlay(incoming: false, roomId: _currentRoomId,
                            peerName: GetRoomDisplayName(_currentRoomId), status: "Calling\u2026");
            await _callService.PlaceCallAsync(_currentRoomId);
        }

        /// <summary>
        /// Phase 1 video smoke test: opens the call overlay and shows a local camera self-preview
        /// (no signalling yet). Verifies that camera capture + WebRTC rendering work on the device.
        /// Hang up stops the preview. Full video calling is wired in a later phase.
        /// </summary>
        private async void VideoCallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoomId == null) return;
            if (_callService == null) return;

            if (!_callService.IsWebRtcAvailable)
            {
                await new Windows.UI.Popups.MessageDialog(
                    "Video calling needs the ARM build of UniMatrix running on the phone.",
                    "Calling unavailable").ShowAsync();
                return;
            }
            if (_callService.InCall) return;

            ShowCallOverlay(incoming: false, roomId: _currentRoomId,
                            peerName: GetRoomDisplayName(_currentRoomId), status: "Camera preview\u2026");
            if (SelfVideoBorder != null) SelfVideoBorder.Visibility = Visibility.Visible;

            bool ok = await _callService.StartLocalPreviewAsync(SelfVideo);
            if (!ok)
            {
                if (SelfVideoBorder != null) SelfVideoBorder.Visibility = Visibility.Collapsed;
                HideCallOverlay();
            }
        }

        private async void CallAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_callService == null) return;
            StopRingVibration();
            if (CallAcceptDeclinePanel != null) CallAcceptDeclinePanel.Visibility = Visibility.Collapsed;
            if (CallActivePanel != null) CallActivePanel.Visibility = Visibility.Visible;
            if (CallStatusText != null) CallStatusText.Text = "Connecting\u2026";
            await _callService.AcceptIncomingAsync();
        }

        private async void CallDeclineButton_Click(object sender, RoutedEventArgs e)
        {
            StopRingVibration();
            HideCallOverlay();
            if (_callService != null) await _callService.HangupAsync();
        }

        private async void CallHangupButton_Click(object sender, RoutedEventArgs e)
        {
            HideCallOverlay();
            if (_callService != null) await _callService.HangupAsync();
        }

        private void CallMuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_callService == null) return;
            bool muted = _callService.ToggleMute();
            UpdateMuteButton(muted);
        }

        private void UpdateMuteButton(bool muted)
        {
            // Glyph E74F = muted mic, E720 = mic. Label flips to the action the button performs.
            if (CallMuteIcon != null) CallMuteIcon.Glyph = muted ? "\uE74F" : "\uE720";
            if (CallMuteLabel != null) CallMuteLabel.Text = muted ? "Unmute" : "Mute";
        }

        // ---- CallService events (raised on the UI thread) ----

        private void CallService_IncomingCall(string roomId)
        {
            ShowCallOverlay(incoming: true, roomId: roomId,
                            peerName: GetRoomDisplayName(roomId), status: "Incoming call\u2026");
            StartRingVibration();
        }

        private void CallService_CallConnected()
        {
            StopRingVibration();
            if (CallAcceptDeclinePanel != null) CallAcceptDeclinePanel.Visibility = Visibility.Collapsed;
            if (CallActivePanel != null) CallActivePanel.Visibility = Visibility.Visible;
            if (CallStatusText != null) CallStatusText.Text = "Connected";
            UpdateMuteButton(_callService != null && _callService.IsMuted);
            StartCallTimer();
        }

        private void CallService_CallEnded()
        {
            StopRingVibration();
            StopCallTimer();
            HideCallOverlay();
        }

        // ---- Incoming-call vibration ----

        /// <summary>
        /// Buzzes the phone in a repeating pattern while a call is ringing. Uses the Windows Phone
        /// vibration device, which only exists on mobile, so the call is guarded with ApiInformation
        /// and wrapped in try/catch to stay a no-op on desktop or if the device has no motor.
        /// </summary>
        private void StartRingVibration()
        {
            if (_ringVibrateTimer != null && _ringVibrateTimer.IsEnabled) return;
            if (!Windows.Foundation.Metadata.ApiInformation.IsTypePresent(
                    "Windows.Phone.Devices.Notification.VibrationDevice")) return;

            VibrateOnce();
            if (_ringVibrateTimer == null)
            {
                _ringVibrateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
                _ringVibrateTimer.Tick += (s, e) => VibrateOnce();
            }
            _ringVibrateTimer.Start();
        }

        private void StopRingVibration()
        {
            if (_ringVibrateTimer != null) _ringVibrateTimer.Stop();
        }

        private void VibrateOnce()
        {
            try
            {
                var device = Windows.Phone.Devices.Notification.VibrationDevice.GetDefault();
                device?.Vibrate(TimeSpan.FromMilliseconds(800));
            }
            catch { /* no vibration motor / not permitted: ignore */ }
        }

        // ---- Call duration timer ----

        private void StartCallTimer()
        {
            // CallConnected can fire more than once (ICE Connected/Completed); keep the original
            // start time so the displayed duration doesn't reset.
            if (_callTimer != null && _callTimer.IsEnabled) return;

            _callConnectedAt = DateTimeOffset.Now;
            if (_callTimer == null)
            {
                _callTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                _callTimer.Tick += CallTimer_Tick;
            }
            UpdateCallTimerText();
            if (CallTimerPanel != null) CallTimerPanel.Visibility = Visibility.Visible;
            _callTimer.Start();
        }

        private void StopCallTimer()
        {
            if (_callTimer != null) _callTimer.Stop();
            if (CallTimerPanel != null) CallTimerPanel.Visibility = Visibility.Collapsed;
            if (CallTimerText != null) CallTimerText.Text = "";
        }

        private void CallTimer_Tick(object sender, object e)
        {
            UpdateCallTimerText();
        }

        private void UpdateCallTimerText()
        {
            if (CallTimerText == null) return;
            TimeSpan elapsed = DateTimeOffset.Now - _callConnectedAt;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            CallTimerText.Text = elapsed.Hours > 0
                ? string.Format("{0}:{1:D2}:{2:D2}", (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds)
                : string.Format("{0:D2}:{1:D2}", elapsed.Minutes, elapsed.Seconds);
        }

        /// <summary>
        /// Reflects the CallService's progress on the call screen. The service raises this on the
        /// UI thread with diagnostic strings; we map the meaningful ones to short user-facing text
        /// so the caller no longer appears frozen on "Calling…" while signalling/ICE is underway.
        /// Unknown strings are ignored so raw debug noise never reaches the screen.
        /// </summary>
        private void CallService_StatusChanged(string status)
        {
            if (CallOverlay == null || CallOverlay.Visibility != Visibility.Visible) return;
            if (CallStatusText == null || string.IsNullOrEmpty(status)) return;

            // Once the accept/decline buttons are gone we're past the ringing stage; don't let a
            // late status overwrite the incoming-call prompt before the user has answered.
            bool ringing = CallAcceptDeclinePanel != null &&
                           CallAcceptDeclinePanel.Visibility == Visibility.Visible;
            if (ringing) return;

            string friendly = null;
            if (status.StartsWith("Calling")) friendly = "Calling\u2026";
            else if (status.StartsWith("TURN servers") || status.StartsWith("No TURN")) friendly = "Connecting\u2026";
            else if (status.StartsWith("Answer received")) friendly = "Connecting\u2026";
            else if (status.StartsWith("ICE state: Checking")) friendly = "Connecting\u2026";
            else if (status.StartsWith("ICE state: Connected") ||
                     status.StartsWith("ICE state: Completed")) friendly = "Connected";

            if (friendly != null) CallStatusText.Text = friendly;
        }

        // ---- Overlay helpers ----

        private void ShowCallOverlay(bool incoming, string roomId, string peerName, string status)
        {
            if (CallOverlay == null) return;
            if (CallPeerName != null) CallPeerName.Text = peerName ?? "";
            if (CallStatusText != null) CallStatusText.Text = status ?? "";
            SetCallAvatar(roomId, peerName);
            if (CallAcceptDeclinePanel != null)
                CallAcceptDeclinePanel.Visibility = incoming ? Visibility.Visible : Visibility.Collapsed;
            // The active-call panel (mute + hang up) shows for an outgoing call right away and for an
            // incoming call once it's accepted.
            if (CallActivePanel != null)
                CallActivePanel.Visibility = incoming ? Visibility.Collapsed : Visibility.Visible;
            // The timer only appears once the call is connected.
            if (CallTimerPanel != null) CallTimerPanel.Visibility = Visibility.Collapsed;
            if (CallTimerText != null) CallTimerText.Text = "";
            UpdateMuteButton(false);
            CallOverlay.Visibility = Visibility.Visible;
        }

        private void HideCallOverlay()
        {
            // Stop a standalone camera preview (Phase 1 smoke test) and hide the self-view. During a
            // real call InCall is true, so the preview teardown is skipped (the call owns the tracks).
            if (_callService != null && !_callService.InCall) _callService.StopLocalPreview();
            if (SelfVideoBorder != null) SelfVideoBorder.Visibility = Visibility.Collapsed;
            if (CallOverlay != null) CallOverlay.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// Fills the call avatar from the room: the room avatar image when one exists, otherwise a
        /// coloured circle with the peer's initial (e.g. "V"). Mirrors the room-info avatar.
        /// </summary>
        private void SetCallAvatar(string roomId, string peerName)
        {
            if (CallAvatarFallback == null) return;

            Room room = null;
            try { room = _db?.GetRoom(roomId); } catch { }

            string initial = room != null ? room.AvatarInitial : null;
            if (string.IsNullOrEmpty(initial))
                initial = string.IsNullOrEmpty(peerName) ? "?" : peerName.Substring(0, 1).ToUpper();
            if (CallAvatarInitial != null) CallAvatarInitial.Text = initial;
            if (room != null) CallAvatarFallback.Fill = room.AvatarBrush;

            if (room != null && room.HasAvatar)
            {
                CallAvatarImage.Fill = new ImageBrush
                {
                    ImageSource = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(room.AvatarUrl)),
                    Stretch = Stretch.UniformToFill
                };
                CallAvatarImage.Visibility = Visibility.Visible;
                if (CallAvatarInitial != null) CallAvatarInitial.Visibility = Visibility.Collapsed;
            }
            else
            {
                CallAvatarImage.Visibility = Visibility.Collapsed;
                if (CallAvatarInitial != null) CallAvatarInitial.Visibility = Visibility.Visible;
            }
        }

        private string GetRoomDisplayName(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return "";
            try
            {
                Room room = _db?.GetRoom(roomId);
                if (room != null && !string.IsNullOrEmpty(room.DisplayName)) return room.DisplayName;
            }
            catch { }
            return roomId;
        }
    }
}
