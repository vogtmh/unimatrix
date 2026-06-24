using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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

            ShowCallOverlay(incoming: false, peerName: GetRoomDisplayName(_currentRoomId),
                            status: "Calling\u2026");
            await _callService.PlaceCallAsync(_currentRoomId);
        }

        private async void CallAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_callService == null) return;
            if (CallAcceptDeclinePanel != null) CallAcceptDeclinePanel.Visibility = Visibility.Collapsed;
            if (CallHangupButton != null) CallHangupButton.Visibility = Visibility.Visible;
            if (CallStatusText != null) CallStatusText.Text = "Connecting\u2026";
            await _callService.AcceptIncomingAsync();
        }

        private async void CallDeclineButton_Click(object sender, RoutedEventArgs e)
        {
            HideCallOverlay();
            if (_callService != null) await _callService.HangupAsync();
        }

        private async void CallHangupButton_Click(object sender, RoutedEventArgs e)
        {
            HideCallOverlay();
            if (_callService != null) await _callService.HangupAsync();
        }

        // ---- CallService events (raised on the UI thread) ----

        private void CallService_IncomingCall(string roomId)
        {
            ShowCallOverlay(incoming: true, peerName: GetRoomDisplayName(roomId),
                            status: "Incoming call\u2026");
        }

        private void CallService_CallConnected()
        {
            if (CallAcceptDeclinePanel != null) CallAcceptDeclinePanel.Visibility = Visibility.Collapsed;
            if (CallHangupButton != null) CallHangupButton.Visibility = Visibility.Visible;
            if (CallStatusText != null) CallStatusText.Text = "Connected";
        }

        private void CallService_CallEnded()
        {
            HideCallOverlay();
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

        private void ShowCallOverlay(bool incoming, string peerName, string status)
        {
            if (CallOverlay == null) return;
            if (CallPeerName != null) CallPeerName.Text = peerName ?? "";
            if (CallStatusText != null) CallStatusText.Text = status ?? "";
            if (CallAcceptDeclinePanel != null)
                CallAcceptDeclinePanel.Visibility = incoming ? Visibility.Visible : Visibility.Collapsed;
            if (CallHangupButton != null)
                CallHangupButton.Visibility = incoming ? Visibility.Collapsed : Visibility.Visible;
            CallOverlay.Visibility = Visibility.Visible;
        }

        private void HideCallOverlay()
        {
            if (CallOverlay != null) CallOverlay.Visibility = Visibility.Collapsed;
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
