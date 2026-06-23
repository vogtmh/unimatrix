using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using UniMatrix.Models;

namespace UniMatrix
{
    /// <summary>
    /// Picture sending: pick an image, show an instant local-echo preview, upload to the
    /// homeserver's content repository, then send it as an m.image event. Mirrors the text
    /// send flow in <see cref="SendCurrentMessage"/>.
    /// </summary>
    public sealed partial class MainPage
    {
        private async void AttachButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRoomId == null) return;

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.Thumbnail,
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".gif");
            picker.FileTypeFilter.Add(".bmp");
            picker.FileTypeFilter.Add(".webp");

            StorageFile file;
            try { file = await picker.PickSingleFileAsync(); }
            catch (Exception ex) { App.Log("Picker EXC: " + ex.Message); return; }
            if (file == null) return;

            await SendImageAsync(file);
        }

        private async Task SendImageAsync(StorageFile file)
        {
            string roomId = _currentRoomId;
            if (roomId == null) return;

            // Read the bytes and probe dimensions (best effort) before we touch the UI.
            IBuffer buffer;
            string contentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            ulong size = 0;
            int width = 0, height = 0;
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                size = props.Size;
                buffer = await FileIO.ReadBufferAsync(file);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not read image: " + ex.Message);
                return;
            }

            try
            {
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    var decoder = await BitmapDecoder.CreateAsync(stream);
                    width = (int)decoder.PixelWidth;
                    height = (int)decoder.PixelHeight;
                }
            }
            catch { /* dimensions are optional metadata */ }

            // Optimistic local echo with an instant preview (copied into the media folder so
            // it survives even though we don't yet know the mxc URI).
            string previewUri = await _media.CacheLocalPreviewAsync(file);
            var echo = new Message
            {
                EventId = "echo_" + Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                Sender = _settings.UserId,
                MsgType = "m.image",
                Body = file.Name,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsLocalEcho = true,
                IsMine = true
            };
            _db.UpsertMessage(echo);
            SetEchoDateSeparator(echo);
            echo.MediaUrl = previewUri;
            Messages.Add(echo);
            ScrollMessagesToBottom();

            try
            {
                string mxc = await _client.UploadMediaAsync(buffer, contentType, file.Name);
                if (string.IsNullOrEmpty(mxc))
                {
                    echo.Body = file.Name + "  (not sent)";
                    await ShowErrorAsync("Could not upload image.");
                    return;
                }

                // Register the freshly-uploaded file under its mxc name so the synced event
                // resolves from cache instead of re-downloading what we just sent.
                await _media.CacheLocalFileForMxcAsync(file, mxc);

                await _client.SendImageMessageAsync(roomId, mxc, file.Name, contentType, width, height, size);
                // The confirmed m.image event arrives via /sync, which removes this echo.
            }
            catch (Exception ex)
            {
                echo.Body = file.Name + "  (not sent)";
                await ShowErrorAsync("Could not send image: " + ex.Message);
            }
        }

        /// <summary>
        /// Sets <see cref="Message.ShowDateSeparator"/> on a local echo so a date pill appears
        /// when it begins a new day (or the timeline was empty). Shared by text and image echoes.
        /// </summary>
        private void SetEchoDateSeparator(Message echo)
        {
            var echoDay = DateTimeOffset.FromUnixTimeMilliseconds(echo.Timestamp).LocalDateTime.Date;
            DateTime? lastDay = Messages.Count > 0
                ? (DateTime?)DateTimeOffset.FromUnixTimeMilliseconds(Messages[Messages.Count - 1].Timestamp).LocalDateTime.Date
                : null;
            echo.ShowDateSeparator = lastDay == null || echoDay != lastDay.Value;
        }

        // ---- Full-screen image viewer ----

        private async void MessageImage_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var img = sender as Image;
            var msg = img?.DataContext as Message;
            if (msg == null) return;
            e.Handled = true;
            await OpenImageViewerAsync(msg);
        }

        private async Task OpenImageViewerAsync(Message msg)
        {
            // Show immediately with the cached thumbnail (if any) so there's instant feedback,
            // then swap in the full-resolution original once it has downloaded.
            ImageViewerScroll.ChangeView(null, null, 1.0f, true);
            ImageViewerImage.Source = null;
            if (!string.IsNullOrEmpty(msg.MediaUrl))
            {
                try { ImageViewerImage.Source = new BitmapImage(new Uri(msg.MediaUrl)); }
                catch { }
            }
            ImageViewerPanel.Visibility = Visibility.Visible;

            if (string.IsNullOrEmpty(msg.Mxc)) return;

            ImageViewerSpinner.IsActive = true;
            try
            {
                string fullUri = await _media.GetFullImageUriAsync(msg.Mxc);
                // Only apply if the viewer is still open and showing this same image.
                if (fullUri != null && ImageViewerPanel.Visibility == Visibility.Visible)
                {
                    ImageViewerImage.Source = new BitmapImage(new Uri(fullUri));
                }
            }
            catch (Exception ex) { App.Log("Viewer EXC: " + ex.Message); }
            finally { ImageViewerSpinner.IsActive = false; }
        }

        private void CloseImageViewer()
        {
            ImageViewerPanel.Visibility = Visibility.Collapsed;
            ImageViewerImage.Source = null;
            ImageViewerSpinner.IsActive = false;
        }

        private void ImageViewerCloseButton_Click(object sender, RoutedEventArgs e) => CloseImageViewer();

        private void ImageViewerPanel_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Tap on the dark backdrop dismisses; taps on the image bubble up here too, but
            // the user expects backdrop-tap-to-close like other photo viewers.
            CloseImageViewer();
        }
    }
}
