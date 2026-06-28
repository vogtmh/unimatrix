using System;
using System.Threading.Tasks;
using Windows.Devices.Geolocation;
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
            ShowAttachSheet();
            await Task.FromResult(0);
        }

        // ---- Attach bottom sheet (image / file / location) ----

        private void ShowAttachSheet()
        {
            AttachScrim.Visibility = Visibility.Visible;
            AttachSheet.Visibility = Visibility.Visible;
            AttachSheetTranslate.Y = 400;
            AttachSheetSlideIn.Begin();
        }

        private Action _attachSheetSlideOutAction;

        private void CloseAttachSheet(Action afterClose = null)
        {
            AttachSheetSlideOut.Completed -= OnAttachSheetSlideOutCompleted;
            _attachSheetSlideOutAction = afterClose;
            AttachSheetSlideOut.Completed += OnAttachSheetSlideOutCompleted;
            AttachSheetSlideOut.Begin();
        }

        private void OnAttachSheetSlideOutCompleted(object sender, object e)
        {
            AttachSheetSlideOut.Completed -= OnAttachSheetSlideOutCompleted;
            AttachSheet.Visibility = Visibility.Collapsed;
            AttachScrim.Visibility = Visibility.Collapsed;
            var act = _attachSheetSlideOutAction;
            _attachSheetSlideOutAction = null;
            act?.Invoke();
        }

        private void AttachScrim_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            CloseAttachSheet();
        }

        private void AttachImageButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAttachSheet(async () => await PickAndSendImageAsync());
        }

        private void AttachFileButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAttachSheet(async () => await PickAndSendFileAsync());
        }

        private void AttachLocationButton_Click(object sender, RoutedEventArgs e)
        {
            CloseAttachSheet(async () => await SendCurrentLocationAsync());
        }

        private async Task PickAndSendImageAsync()
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
                if (RoomNeedsEncryption(roomId))
                {
                    await SendEncryptedImageAsync(roomId, file, buffer, contentType, width, height, size, echo);
                    return;
                }

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
        /// Encrypts a picked image, uploads the ciphertext, persists its decryption key against the
        /// resulting mxc, and sends an m.image event carrying the encrypted-attachment "file" block
        /// (wrapped in Megolm by <see cref="SendRoomMessageAsync"/>). Mirrors the plaintext path.
        /// </summary>
        private async Task SendEncryptedImageAsync(string roomId, StorageFile file,
            Windows.Storage.Streams.IBuffer buffer, string contentType, int width, int height, ulong size, Message echo)
        {
            byte[] plain;
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out plain);

            var enc = Services.AttachmentCrypto.Encrypt(plain);
            var cipherBuf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(enc.Ciphertext);

            string mxc = await _client.UploadMediaAsync(cipherBuf, "application/octet-stream", null);
            if (string.IsNullOrEmpty(mxc))
            {
                echo.Body = file.Name + "  (not sent)";
                await ShowErrorAsync("Could not upload image.");
                return;
            }

            // Fill in the mxc and persist the key so we (and the media layer) can decrypt later.
            enc.FileInfo["url"] = Windows.Data.Json.JsonValue.CreateStringValue(mxc);
            _db.SaveAttachmentKey(mxc, enc.FileInfo.Stringify());

            // Our own decrypted copy is already on disk; bind it to the mxc for instant display.
            await _media.CacheLocalFileForMxcAsync(file, mxc);

            var info = new Windows.Data.Json.JsonObject();
            if (!string.IsNullOrEmpty(contentType)) info["mimetype"] = Windows.Data.Json.JsonValue.CreateStringValue(contentType);
            if (width > 0) info["w"] = Windows.Data.Json.JsonValue.CreateNumberValue(width);
            if (height > 0) info["h"] = Windows.Data.Json.JsonValue.CreateNumberValue(height);
            if (size > 0) info["size"] = Windows.Data.Json.JsonValue.CreateNumberValue(size);

            var content = new Windows.Data.Json.JsonObject
            {
                ["msgtype"] = Windows.Data.Json.JsonValue.CreateStringValue("m.image"),
                ["body"] = Windows.Data.Json.JsonValue.CreateStringValue(string.IsNullOrEmpty(file.Name) ? "image" : file.Name),
                ["file"] = enc.FileInfo,
                ["info"] = info
            };

            await SendRoomMessageAsync(roomId, content);
            // The confirmed m.room.encrypted event arrives via /sync, which removes this echo.
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

        /// <summary>
        /// Press-and-hold "Copy" on a text bubble: puts the message body on the clipboard. Useful
        /// for grabbing the raw text of a bubble (including the encrypted-event JSON that shows up
        /// for call signalling we can't yet render) to paste elsewhere.
        /// </summary>
        private void MessageCopy_Click(object sender, RoutedEventArgs e)
        {
            var item = sender as MenuFlyoutItem;
            var msg = item?.DataContext as Message;
            if (msg == null || string.IsNullOrEmpty(msg.Body)) return;

            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(msg.Body);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
        }

        private async Task OpenImageViewerAsync(Message msg)
        {
            // Show immediately with the cached thumbnail (if any) so there's instant feedback,
            // then swap in the full-resolution original once it has downloaded.
            ImageViewerScroll.ChangeView(null, null, 1.0f, true);
            ConstrainViewerImageToScreen();
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

        private void ImageViewerPanel_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ConstrainViewerImageToScreen();
        }

        /// <summary>
        /// Caps the image to the panel size so a large photo fits the screen at zoom 1x (with
        /// Stretch=Uniform the Image otherwise sizes to its full pixel dimensions and overflows).
        /// Pinch-zoom still scales the ScrollViewer content beyond this, so the user can zoom in.
        /// </summary>
        private void ConstrainViewerImageToScreen()
        {
            double w = ImageViewerPanel.ActualWidth;
            double h = ImageViewerPanel.ActualHeight;
            if (w > 0) ImageViewerImage.MaxWidth = w;
            if (h > 0) ImageViewerImage.MaxHeight = h;
        }

        private void ImageViewerPanel_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            // Tap on the dark backdrop dismisses; taps on the image bubble up here too, but
            // the user expects backdrop-tap-to-close like other photo viewers.
            CloseImageViewer();
        }
        // ---- Full-screen location map viewer ----

        // The coordinates awaiting display; pushed into the WebView once it has finished loading.
        private double _pendingMapLat, _pendingMapLon;
        private bool _mapWebViewReady;

        private void MessageMap_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var grid = sender as Grid;
            var msg = grid?.DataContext as Message;
            if (msg == null || !msg.IsLocation) return;
            e.Handled = true;
            OpenMapViewer(msg.Latitude, msg.Longitude);
        }

        private void OpenMapViewer(double lat, double lon)
        {
            _pendingMapLat = lat;
            _pendingMapLon = lon;
            MapViewerPanel.Visibility = Visibility.Visible;

            if (!_mapWebViewReady)
            {
                // First open: load the bundled Leaflet page, then push coordinates from
                // NavigationCompleted (the script isn't callable until the page is ready).
                MapWebView.NavigationCompleted += MapWebView_NavigationCompleted;
                MapWebView.Source = new Uri("ms-appx-web:///Assets/map/map.html");
            }
            else
            {
                PushMapLocation();
            }
        }

        private void MapWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            _mapWebViewReady = args.IsSuccess;
            if (args.IsSuccess) PushMapLocation();
        }

        private async void PushMapLocation()
        {
            try
            {
                await MapWebView.InvokeScriptAsync("setLocation", new[]
                {
                    _pendingMapLat.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _pendingMapLon.ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
            }
            catch (Exception ex) { App.Log("Map viewer EXC: " + ex.Message); }
        }

        private void CloseMapViewer()
        {
            MapViewerPanel.Visibility = Visibility.Collapsed;
        }

        private void MapViewerCloseButton_Click(object sender, RoutedEventArgs e) => CloseMapViewer();

        // ---- Send current location (m.location) ----

        /// <summary>
        /// Reads the device's current position and sends it as an m.location event (geo_uri =
        /// "geo:lat,lon"). Works in both plaintext and encrypted rooms via <see cref="SendRoomMessageAsync"/>.
        /// Shows an optimistic local-echo location bubble that the confirmed event replaces via /sync.
        /// </summary>
        private async Task SendCurrentLocationAsync()
        {
            string roomId = _currentRoomId;
            if (roomId == null) return;

            Geoposition pos;
            try
            {
                var access = await Geolocator.RequestAccessAsync();
                if (access != GeolocationAccessStatus.Allowed)
                {
                    await ShowErrorAsync("Location access is turned off. Enable it in Settings to share your location.");
                    return;
                }

                var locator = new Geolocator { DesiredAccuracyInMeters = 50 };
                pos = await locator.GetGeopositionAsync(TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(20));
            }
            catch (Exception ex)
            {
                App.Log("Location EXC: " + ex.Message);
                await ShowErrorAsync("Could not get your location: " + ex.Message);
                return;
            }

            if (pos?.Coordinate?.Point == null)
            {
                await ShowErrorAsync("Could not determine your location.");
                return;
            }

            double lat = pos.Coordinate.Point.Position.Latitude;
            double lon = pos.Coordinate.Point.Position.Longitude;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string geoUri = "geo:" + lat.ToString(inv) + "," + lon.ToString(inv);
            // The wire body is plain ("Location"); both receive paths add the 📍 prefix. The echo
            // uses that same display form so it de-duplicates against the confirmed event.
            string wireBody = "Location";
            string displayBody = "\uD83D\uDCCD Location";

            // Optimistic local echo (renders the inline map immediately).
            var echo = new Message
            {
                EventId = "echo_" + Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                Sender = _settings.UserId,
                MsgType = "m.location",
                Body = displayBody,
                Mxc = geoUri,   // the model reads geo_uri from Mxc
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsLocalEcho = true,
                IsMine = true
            };
            _db.UpsertMessage(echo);
            SetEchoDateSeparator(echo);
            Messages.Add(echo);
            ScrollMessagesToBottom();

            try
            {
                var content = new Windows.Data.Json.JsonObject
                {
                    ["msgtype"] = Windows.Data.Json.JsonValue.CreateStringValue("m.location"),
                    ["body"] = Windows.Data.Json.JsonValue.CreateStringValue(wireBody),
                    ["geo_uri"] = Windows.Data.Json.JsonValue.CreateStringValue(geoUri)
                };
                await SendRoomMessageAsync(roomId, content);
                // The confirmed event arrives via /sync, which removes this echo.
            }
            catch (Exception ex)
            {
                echo.Body = displayBody + "  (not sent)";
                await ShowErrorAsync("Could not send location: " + ex.Message);
            }
        }

        // ---- Send a file (m.file) ----

        private async Task PickAndSendFileAsync()
        {
            if (_currentRoomId == null) return;

            var picker = new FileOpenPicker
            {
                ViewMode = PickerViewMode.List,
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            // "*" allows any file type.
            picker.FileTypeFilter.Add("*");

            StorageFile file;
            try { file = await picker.PickSingleFileAsync(); }
            catch (Exception ex) { App.Log("File picker EXC: " + ex.Message); return; }
            if (file == null) return;

            await SendFileAsync(file);
        }

        /// <summary>
        /// Uploads a picked file and sends it as an m.file event (encrypted-attachment block in
        /// encrypted rooms). Shows an optimistic local-echo file card that the confirmed event
        /// replaces via /sync. Mirrors <see cref="SendImageAsync"/> but without image probing.
        /// </summary>
        private async Task SendFileAsync(StorageFile file)
        {
            string roomId = _currentRoomId;
            if (roomId == null) return;

            IBuffer buffer;
            string contentType = string.IsNullOrEmpty(file.ContentType) ? "application/octet-stream" : file.ContentType;
            ulong size = 0;
            try
            {
                var props = await file.GetBasicPropertiesAsync();
                size = props.Size;
                buffer = await FileIO.ReadBufferAsync(file);
            }
            catch (Exception ex)
            {
                await ShowErrorAsync("Could not read file: " + ex.Message);
                return;
            }

            // Optimistic local echo (file card with the filename).
            var echo = new Message
            {
                EventId = "echo_" + Guid.NewGuid().ToString("N"),
                RoomId = roomId,
                Sender = _settings.UserId,
                MsgType = "m.file",
                Body = file.Name,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsLocalEcho = true,
                IsMine = true
            };
            _db.UpsertMessage(echo);
            SetEchoDateSeparator(echo);
            Messages.Add(echo);
            ScrollMessagesToBottom();

            try
            {
                if (RoomNeedsEncryption(roomId))
                {
                    await SendEncryptedFileAsync(roomId, file, buffer, contentType, size, echo);
                    return;
                }

                string mxc = await _client.UploadMediaAsync(buffer, contentType, file.Name);
                if (string.IsNullOrEmpty(mxc))
                {
                    echo.Body = file.Name + "  (not sent)";
                    await ShowErrorAsync("Could not upload file.");
                    return;
                }

                var info = new Windows.Data.Json.JsonObject();
                if (!string.IsNullOrEmpty(contentType)) info["mimetype"] = Windows.Data.Json.JsonValue.CreateStringValue(contentType);
                if (size > 0) info["size"] = Windows.Data.Json.JsonValue.CreateNumberValue(size);

                var content = new Windows.Data.Json.JsonObject
                {
                    ["msgtype"] = Windows.Data.Json.JsonValue.CreateStringValue("m.file"),
                    ["body"] = Windows.Data.Json.JsonValue.CreateStringValue(file.Name),
                    ["filename"] = Windows.Data.Json.JsonValue.CreateStringValue(file.Name),
                    ["url"] = Windows.Data.Json.JsonValue.CreateStringValue(mxc),
                    ["info"] = info
                };
                await SendRoomMessageAsync(roomId, content);
                // The confirmed event arrives via /sync, which removes this echo.
            }
            catch (Exception ex)
            {
                echo.Body = file.Name + "  (not sent)";
                await ShowErrorAsync("Could not send file: " + ex.Message);
            }
        }

        /// <summary>
        /// Encrypts a picked file, uploads the ciphertext, persists its decryption key against the
        /// resulting mxc, and sends an m.file event carrying the encrypted-attachment "file" block.
        /// Mirrors <see cref="SendEncryptedImageAsync"/>.
        /// </summary>
        private async Task SendEncryptedFileAsync(string roomId, StorageFile file,
            IBuffer buffer, string contentType, ulong size, Message echo)
        {
            byte[] plain;
            Windows.Security.Cryptography.CryptographicBuffer.CopyToByteArray(buffer, out plain);

            var enc = Services.AttachmentCrypto.Encrypt(plain);
            var cipherBuf = Windows.Security.Cryptography.CryptographicBuffer.CreateFromByteArray(enc.Ciphertext);

            string mxc = await _client.UploadMediaAsync(cipherBuf, "application/octet-stream", null);
            if (string.IsNullOrEmpty(mxc))
            {
                echo.Body = file.Name + "  (not sent)";
                await ShowErrorAsync("Could not upload file.");
                return;
            }

            enc.FileInfo["url"] = Windows.Data.Json.JsonValue.CreateStringValue(mxc);
            _db.SaveAttachmentKey(mxc, enc.FileInfo.Stringify());

            var info = new Windows.Data.Json.JsonObject();
            if (!string.IsNullOrEmpty(contentType)) info["mimetype"] = Windows.Data.Json.JsonValue.CreateStringValue(contentType);
            if (size > 0) info["size"] = Windows.Data.Json.JsonValue.CreateNumberValue(size);

            var content = new Windows.Data.Json.JsonObject
            {
                ["msgtype"] = Windows.Data.Json.JsonValue.CreateStringValue("m.file"),
                ["body"] = Windows.Data.Json.JsonValue.CreateStringValue(file.Name),
                ["filename"] = Windows.Data.Json.JsonValue.CreateStringValue(file.Name),
                ["file"] = enc.FileInfo,
                ["info"] = info
            };

            await SendRoomMessageAsync(roomId, content);
            // The confirmed event arrives via /sync, which removes this echo.
        }

        // ---- Receive a file: download (decrypting if needed) and open ----

        private async void MessageFile_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var fe = sender as FrameworkElement;
            var msg = fe?.DataContext as Message;
            if (msg == null || !msg.IsFile) return;
            e.Handled = true;
            if (string.IsNullOrEmpty(msg.Mxc)) return; // unconfirmed echo: nothing to download yet

            try
            {
                var file = await _media.DownloadToFileAsync(msg.Mxc, msg.Body);
                if (file == null)
                {
                    await ShowErrorAsync("Could not download file.");
                    return;
                }
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch (Exception ex)
            {
                App.Log("File open EXC: " + ex.Message);
                await ShowErrorAsync("Could not open file: " + ex.Message);
            }
        }
    }
}

