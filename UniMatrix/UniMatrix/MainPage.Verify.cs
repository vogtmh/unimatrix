using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using UniMatrix.Services;

namespace UniMatrix
{
    public sealed partial class MainPage
    {
        // The transaction id of the verification currently shown in the overlay.
        private string _verifyTxnId;

        /// <summary>Routes VerificationService callbacks (raised on a background sync thread) onto the
        /// UI thread to drive the verification overlay.</summary>
        private void WireVerificationCallbacks()
        {
            if (_verify == null) return;
            _verify.OnIncomingRequest = req => RunOnUi(() => ShowVerifyRequest(req));
            _verify.OnShowSas = (txn, emojis, dec) => RunOnUi(() => ShowVerifySas(txn, emojis, dec));
            _verify.OnComplete = (txn, ok, msg) => RunOnUi(() => ShowVerifyResult(txn, ok, msg));
        }

        private void RunOnUi(Action action)
        {
            var ignore = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                try { action(); } catch (Exception ex) { App.Log("VERIFY UI: " + ex.Message); }
            });
        }

        // ---- Overlay stages ----

        private void ShowVerifyRequest(VerificationRequest req)
        {
            if (req == null) return;
            _verifyTxnId = req.TransactionId;
            VerifyTitle.Text = "Verify session";
            VerifySubtitle.Text = req.UserId + " wants to verify a session (" + req.DeviceId + ").";
            VerifyRequestPanel.Visibility = Visibility.Visible;
            VerifyEmojiPanel.Visibility = Visibility.Collapsed;
            VerifyStatusPanel.Visibility = Visibility.Collapsed;
            VerifyOverlay.Visibility = Visibility.Visible;
            try { VibrateOnce(); } catch { }
        }

        private void ShowVerifySas(string txn, System.Collections.Generic.IList<SasEmoji> emojis, string dec)
        {
            _verifyTxnId = txn;
            VerifyTitle.Text = "Compare emoji";
            VerifySubtitle.Text = "Confirm the emoji match what the other device shows.";

            VerifyEmojiList.Items.Clear();
            if (emojis != null)
                foreach (var e in emojis)
                    VerifyEmojiList.Items.Add(BuildEmojiCell(e));

            VerifyDecimalText.Text = string.IsNullOrEmpty(dec) ? "" : "Numbers: " + dec;

            VerifyRequestPanel.Visibility = Visibility.Collapsed;
            VerifyStatusPanel.Visibility = Visibility.Collapsed;
            VerifyEmojiPanel.Visibility = Visibility.Visible;
            VerifyOverlay.Visibility = Visibility.Visible;
        }

        private void ShowVerifyResult(string txn, bool ok, string msg)
        {
            // Ignore late results for an already-replaced transaction.
            if (!string.IsNullOrEmpty(_verifyTxnId) && txn != _verifyTxnId && VerifyOverlay.Visibility != Visibility.Visible)
                return;

            VerifyRequestPanel.Visibility = Visibility.Collapsed;
            VerifyEmojiPanel.Visibility = Visibility.Collapsed;
            VerifyProgress.IsActive = false;
            VerifyStatusText.Text = ok ? "Verified \u2713\n" + msg : "Verification failed\n" + msg;
            VerifyCloseButton.Visibility = Visibility.Visible;
            VerifyStatusPanel.Visibility = Visibility.Visible;
            VerifyOverlay.Visibility = Visibility.Visible;
        }

        private void ShowVerifyWorking(string text)
        {
            VerifyRequestPanel.Visibility = Visibility.Collapsed;
            VerifyEmojiPanel.Visibility = Visibility.Collapsed;
            VerifyStatusText.Text = text;
            VerifyProgress.IsActive = true;
            VerifyCloseButton.Visibility = Visibility.Collapsed;
            VerifyStatusPanel.Visibility = Visibility.Visible;
            VerifyOverlay.Visibility = Visibility.Visible;
        }

        private void CloseVerifyOverlay()
        {
            VerifyOverlay.Visibility = Visibility.Collapsed;
            VerifyProgress.IsActive = false;
            _verifyTxnId = null;
        }

        private FrameworkElement BuildEmojiCell(SasEmoji e)
        {
            var panel = new StackPanel
            {
                Width = 84,
                Margin = new Thickness(4),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            panel.Children.Add(new TextBlock
            {
                Text = e.Emoji,
                FontSize = 40,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = e.Name,
                FontSize = 12,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });
            return panel;
        }

        // ---- Button handlers ----

        private async void VerifyAcceptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_verify == null || string.IsNullOrEmpty(_verifyTxnId)) return;
            ShowVerifyWorking("Waiting for the other device\u2026");
            await _verify.AcceptIncomingAsync(_verifyTxnId);
        }

        private async void VerifyDeclineButton_Click(object sender, RoutedEventArgs e)
        {
            string txn = _verifyTxnId;
            CloseVerifyOverlay();
            if (_verify != null && !string.IsNullOrEmpty(txn))
                await _verify.CancelAsync(txn, "m.user", "declined");
        }

        private async void VerifyMatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_verify == null || string.IsNullOrEmpty(_verifyTxnId)) return;
            ShowVerifyWorking("Verifying\u2026");
            await _verify.ConfirmSasAsync(_verifyTxnId);
        }

        private async void VerifyMismatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_verify == null || string.IsNullOrEmpty(_verifyTxnId)) return;
            ShowVerifyWorking("Cancelling\u2026");
            await _verify.CancelAsync(_verifyTxnId, "m.mismatched_sas", "emoji did not match");
        }

        private void VerifyCloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseVerifyOverlay();
        }
    }
}
