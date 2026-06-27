using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using UniMatrix.Data;
using UniMatrix.Models;
using UniMatrix.Services;

namespace UniMatrix
{
    public sealed partial class MainPage
    {
        // The end-to-end encryption engine. Null until a session is established; on non-ARM builds
        // its Available property is false and every call is a safe no-op (rooms stay plaintext).
        private CryptoService _crypto;

        // Server-side Megolm key backup. Created alongside _crypto; uploads new inbound sessions and
        // can restore history from a recovery key.
        private KeyBackupService _backup;

        // Secret storage (SSSS): stores/recovers the backup private key via recovery key or passphrase.
        private SsssService _ssss;

        // Guards the one-time "a backup exists, want to restore it?" prompt so it appears at most once.
        private bool _recoveryPromptShown;

        /// <summary>
        /// Creates and initializes the CryptoService once per signed-in session (idempotent).
        /// Loads or creates the Olm account, publishes device keys + one-time keys on first run.
        /// Safe to call from every entry point (session restore, fresh login, setup completion).
        /// </summary>
        private async Task EnsureCryptoAsync()
        {
            if (_crypto != null) return;
            if (_db == null || _client == null || _settings == null) return;
            if (string.IsNullOrEmpty(_settings.UserId) || string.IsNullOrEmpty(_client.AccessToken)) return;

            try
            {
                _crypto = new CryptoService(_db, _client, _settings);
                await _crypto.InitializeAsync();
                App.Log("CRYPTO: EnsureCryptoAsync available=" + _crypto.Available);

                if (_crypto.Available)
                {
                    _backup = new KeyBackupService(_db, _client, _crypto);
                    // As new inbound Megolm sessions arrive, push them to the backup (fire-and-forget).
                    _crypto.OnInboundSessionAdded = s =>
                    {
                        try { var ignore = _backup.BackupSessionAsync(s); } catch { }
                    };
                    await _backup.LoadAsync();
                    App.Log("CRYPTO: backup enabled=" + _backup.Enabled + " locked=" + _backup.ExistsButLocked);

                    _ssss = new SsssService(_client, _backup);
                }
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: EnsureCryptoAsync failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Applies the encryption-related parts of a /sync result: refreshes changed device lists,
        /// processes to-device key deliveries, decrypts new encrypted timeline events, retries
        /// previously-undecryptable messages whose key just arrived, and tops up one-time keys.
        /// Any rooms whose contents changed are added to <paramref name="result"/>.ChangedRooms so
        /// the existing refresh path repaints them.
        /// </summary>
        private async Task ProcessCryptoSyncAsync(SyncResult result)
        {
            if (_crypto == null || !_crypto.Available || result == null) return;

            try
            {
                // Steps 1-5 are CPU-heavy native libolm work (device-key updates, Olm to-device
                // decryption, Megolm decryption loops, one-time-key generation). On the slow Lumia
                // ARM CPU these block for seconds, so run them on a background thread instead of the
                // UI thread (which would freeze the app — even past the point of being closable).
                // The DB is thread-safe (its own _gate lock) and the sync loop is serialized, so no
                // crypto/DB state is touched concurrently. Only the UI-touching step 6 stays on the
                // UI thread, after this await resumes on it.
                await Task.Run(async () =>
                {
                    // 1. Device-list deltas.
                    if (result.DeviceListChanged.Count > 0)
                    {
                        _crypto.MarkUsersDirty(result.DeviceListChanged);
                        await _crypto.UpdateDeviceKeysAsync(result.DeviceListChanged);
                    }
                    foreach (var left in result.DeviceListLeft)
                    {
                        try { _db.DeleteDevicesForUser(left); } catch { }
                    }

                    // 2. To-device messages (Olm) carry room keys + secrets. Returns rooms that gained
                    //    a Megolm session, so we can retry their stored ciphertexts.
                    HashSet<string> newKeyRooms = await _crypto.HandleToDeviceEventsAsync(result.ToDeviceEvents);

                    // 3. Decrypt the encrypted timeline events from this sync.
                    foreach (var enc in result.EncryptedEvents)
                    {
                        if (DecryptAndStore(enc.RoomId, enc.EventId, enc.Sender, enc.Timestamp, enc.Content))
                            result.ChangedRooms.Add(enc.RoomId);
                    }

                    // 4. Retry any earlier undecryptable rows in rooms that just got a key.
                    if (newKeyRooms != null)
                    {
                        foreach (var roomId in newKeyRooms)
                        {
                            if (RetryDecryptRoom(roomId))
                                result.ChangedRooms.Add(roomId);
                        }
                    }

                    // 5. Replenish one-time keys when the server reports a low count.
                    if (result.OneTimeKeyCount.HasValue)
                        await _crypto.EnsureOneTimeKeysAsync(result.OneTimeKeyCount.Value);
                });

                // 6. One-time nudge: if the server holds a backup we haven't unlocked, offer recovery.
                //    UI work — runs on the UI thread (the await above resumed here).
                if (!_recoveryPromptShown && _backup != null && _backup.ExistsButLocked)
                {
                    _recoveryPromptShown = true;
                    RecoveryInput.Text = "";
                    RecoveryProgress.Visibility = Windows.UI.Xaml.Visibility.Collapsed;
                    RecoveryPanel.Visibility = Windows.UI.Xaml.Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: ProcessCryptoSyncAsync error: " + ex.Message);
            }
        }
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: ProcessCryptoSyncAsync error: " + ex.Message);
            }
        }

        /// <summary>Decrypts one encrypted event and overwrites its stored row. Returns true on success.</summary>
        private bool DecryptAndStore(string roomId, string eventId, string sender, long ts, JsonObject content)
        {
            if (_crypto == null || !_crypto.Available || content == null) return false;
            try
            {
                var dr = _crypto.DecryptRoomEvent(roomId, content);
                if (!dr.Ok || dr.ClearContent == null) return false;
                return StoreClearEvent(roomId, eventId, sender, ts, dr.ClearType, dr.ClearContent);
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: decrypt " + eventId + " failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>Re-attempts decryption of every still-encrypted row in a room (key just arrived).</summary>
        private bool RetryDecryptRoom(string roomId)
        {
            if (_crypto == null || !_crypto.Available) return false;
            bool any = false;
            try
            {
                foreach (var kv in _db.GetEncryptedMessages(roomId))
                {
                    JsonObject content;
                    if (!JsonObject.TryParse(kv.Value, out content)) continue;
                    var dr = _crypto.DecryptRoomEvent(roomId, content);
                    if (dr.Ok && dr.ClearContent != null)
                    {
                        // Sender/ts are preserved on the existing row; pass null/0 so the stored
                        // values aren't clobbered by StoreClearEvent (it reads the row first).
                        if (StoreClearEvent(roomId, kv.Key, null, 0, dr.ClearType, dr.ClearContent))
                            any = true;
                    }
                }
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: retry room " + roomId + " failed: " + ex.Message);
            }
            return any;
        }

        /// <summary>
        /// Persists a decrypted inner event as a normal message row, overwriting the encrypted
        /// placeholder. Only the message types the app renders (m.text / m.notice / m.image) are
        /// stored; anything else leaves the encrypted row untouched. Returns true if a row was
        /// written.
        /// </summary>
        private bool StoreClearEvent(string roomId, string eventId, string sender, long ts, string clearType, JsonObject clearContent)
        {
            if (clearType != "m.room.message" || clearContent == null) return false;

            string msgType = MatrixClient.GetString(clearContent, "msgtype");
            if (msgType != "m.text" && msgType != "m.notice" && msgType != "m.image") return false;

            // Recover sender/timestamp from the existing encrypted row when not supplied (retry path).
            var existing = _db.GetMessageById(eventId);
            if (string.IsNullOrEmpty(sender) && existing != null) sender = existing.Sender;
            if (ts == 0 && existing != null) ts = existing.Timestamp;

            string body = MatrixClient.GetString(clearContent, "body");
            string mxc = null;
            if (msgType == "m.image")
            {
                // Plaintext url, or the encrypted-attachment file block's url. For the encrypted
                // case we persist the file block (key/iv/hash) so the media layer can decrypt the
                // downloaded blob.
                mxc = MatrixClient.GetString(clearContent, "url");
                if (string.IsNullOrEmpty(mxc))
                {
                    var file = CryptoService.GetObj(clearContent, "file");
                    if (file != null)
                    {
                        mxc = MatrixClient.GetString(file, "url");
                        if (!string.IsNullOrEmpty(mxc)) _db.SaveAttachmentKey(mxc, file.Stringify());
                    }
                }
            }

            _db.UpsertMessage(new Message
            {
                EventId = eventId,
                RoomId = roomId,
                Sender = sender,
                MsgType = msgType,
                Body = body,
                Timestamp = ts,
                Mxc = mxc,
                IsLocalEcho = false
            });
            return true;
        }

        // ---- Outgoing encryption ----

        /// <summary>True if the room is encrypted and we can encrypt for it.</summary>
        private bool RoomNeedsEncryption(string roomId)
        {
            return _crypto != null && _crypto.Available && _crypto.IsRoomEncrypted(roomId);
        }

        /// <summary>Shares the current Megolm room key with every member device before sending.</summary>
        private async Task ShareRoomKeysAsync(string roomId)
        {
            if (!RoomNeedsEncryption(roomId)) return;
            var members = new List<string>(_db.GetMemberNames(roomId).Keys);
            if (!members.Contains(_settings.UserId)) members.Add(_settings.UserId);
            await _crypto.EnsureRoomKeysSharedAsync(roomId, members);
        }

        /// <summary>
        /// Sends a message event into a room, encrypting it with Megolm when the room is encrypted.
        /// Returns the result of the underlying send (event id), or throws on encryption failure
        /// (we never silently fall back to plaintext in an encrypted room).
        /// </summary>
        private async Task SendRoomMessageAsync(string roomId, JsonObject content)
        {
            if (RoomNeedsEncryption(roomId))
            {
                await ShareRoomKeysAsync(roomId);
                var encrypted = _crypto.EncryptRoomEvent(roomId, "m.room.message", content);
                if (encrypted == null) throw new Exception("Encryption failed");
                await _client.SendEventAsync(roomId, "m.room.encrypted", encrypted);
                return;
            }

            await _client.SendEventAsync(roomId, "m.room.message", content);
        }
    }
}
