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

        // Interactive SAS (emoji) device verification over to-device events. Created with _crypto.
        private VerificationService _verify;

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

                    _verify = new VerificationService(_client, _crypto, _db);
                    WireVerificationCallbacks();
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

                    // 2b. Plaintext SAS verification events (m.key.verification.*) drive the verifier.
                    if (_verify != null && result.ToDeviceEvents != null)
                    {
                        foreach (var ev in result.ToDeviceEvents)
                        {
                            try
                            {
                                string t = MatrixClient.GetString(ev, "type");
                                if (string.IsNullOrEmpty(t) || !t.StartsWith("m.key.verification.")) continue;
                                string sender = MatrixClient.GetString(ev, "sender");
                                var c = CryptoService.GetObj(ev, "content");
                                if (c != null) await _verify.HandleEventAsync(t, sender, c);
                            }
                            catch (Exception vex) { App.Log("VERIFY: route failed: " + vex.Message); }
                        }
                    }

                    // 3. Decrypt the encrypted timeline events from this sync.
                    foreach (var enc in result.EncryptedEvents)
                    {
                        if (DecryptAndStore(result, enc.RoomId, enc.EventId, enc.Sender, enc.Timestamp, enc.Content))
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

        /// <summary>Decrypts one encrypted event and overwrites its stored row. Returns true on success.</summary>
        private bool DecryptAndStore(SyncResult result, string roomId, string eventId, string sender, long ts, JsonObject content)
        {
            if (_crypto == null || !_crypto.Available || content == null) return false;
            try
            {
                var dr = _crypto.DecryptRoomEvent(roomId, content);
                if (!dr.Ok || dr.ClearContent == null)
                {
                    // Log the reason so undecryptable events (especially call signalling, which
                    // otherwise silently renders as a ciphertext blob) are diagnosable. "no session"
                    // means the Megolm key hasn't arrived yet — step 4's retry will pick it up once
                    // the to-device key lands.
                    App.Log("CRYPTO: decrypt " + eventId + " not ok: " + (dr.FailureReason ?? "?"));
                    return false;
                }

                try
                {
                    string dc = dr.ClearContent.Stringify();
                    if (dc != null && dc.Length > 900) dc = dc.Substring(0, 900) + "...(" + dc.Length + " chars)";
                    App.Log("RX-decrypted " + roomId + " type=" + (dr.ClearType ?? "?") +
                            " sender=" + (sender ?? "?") + " id=" + (eventId ?? "?") + " content=" + dc);
                }
                catch { }

                // Encrypted VoIP signalling: surface it to the CallService (the sync loop dispatches
                // result.CallSignals on the UI thread, where the WebRTC queue lives) and remove the
                // ciphertext placeholder row so it doesn't render as an unreadable blob.
                if (!string.IsNullOrEmpty(dr.ClearType) && dr.ClearType.StartsWith("m.call."))
                {
                    App.Log("CALL: live-decrypted " + dr.ClearType + " from " + (sender ?? "?") + " -> routing");
                    result.CallSignals.Add(new CallSignal
                    {
                        RoomId = roomId,
                        Type = dr.ClearType,
                        Sender = sender,
                        Timestamp = ts,
                        Content = dr.ClearContent
                    });
                    _db.DeleteMessage(eventId);
                    return true; // the room changed (placeholder removed)
                }

                // Encrypted MatrixRTC (Element Call) ring: surface it for the incoming-call ring and
                // record a timeline tile, then drop the ciphertext placeholder. UniMatrix can't join
                // the LiveKit media, so the call itself is answered in Element.
                if (!string.IsNullOrEmpty(dr.ClearType) && SyncProcessor.IsRtcNotificationType(dr.ClearType))
                {
                    App.Log("CALL: live-decrypted MatrixRTC notification from " + (sender ?? "?") + " -> ring");
                    // One-off: log the cleartext content shape so the assumed MSC4075 fields
                    // (notification_type / lifetime / m.relates_to) can be verified on-device.
                    try { App.Log("RTC: notification content=" + dr.ClearContent.Stringify()); } catch { }
                    var n = SyncProcessor.ParseRtcNotification(roomId, eventId, sender, ts, dr.ClearContent);
                    if (n != null)
                    {
                        result.MatrixRtcNotifications.Add(n);
                        var tile = SyncProcessor.BuildRtcMissedTile(roomId, n.ParentId ?? eventId, sender, ts, _settings?.UserId);
                        if (tile != null)
                        {
                            _db.UpsertMessage(tile);
                            // Replace the "🔒 Encrypted message" room-list preview with the missed-call
                            // label (mirrors the plaintext RTC path and the decrypted-message path), so
                            // a missed call in an encrypted room no longer shows as ciphertext.
                            _db.SetRoomPreviewIfLatest(roomId, tile.Timestamp, tile.Body);
                        }
                    }
                    _db.DeleteMessage(eventId);
                    return true;
                }

                if (StoreClearEvent(roomId, eventId, sender, ts, dr.ClearType, dr.ClearContent))
                    return true;

                // Decrypted OK but not a displayable message (reaction/relation/redaction/state, or
                // a non-legacy call format such as Element Call / MSC3401). Remove the ciphertext
                // placeholder so it stops rendering as an unreadable blob, and log the cleartype so
                // unhandled event types — especially call signalling — are diagnosable.
                App.Log("CRYPTO: decrypted " + eventId + " type=" + (dr.ClearType ?? "?") +
                        " not displayable -> removing blob");
                _db.DeleteMessage(eventId);
                return true;
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: decrypt " + eventId + " failed: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Delivers a decrypted call signalling event to the CallService on the UI thread (the
        /// WebRTC event queue is bound to it). Used by the retry path, which runs on a background
        /// thread. OnRemoteInvite applies its own 60s staleness guard, so replaying old history here
        /// is harmless — only a genuinely fresh invite rings.
        /// </summary>
        private void RouteDecryptedCallSignal(string roomId, string clearType, string sender, long ts, JsonObject clearContent)
        {
            if (_callService == null || clearContent == null) return;
            var sig = new CallSignal
            {
                RoomId = roomId,
                Type = clearType,
                Sender = sender,
                Timestamp = ts,
                Content = clearContent
            };
            var ignore = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                try { await _callService.HandleSignalAsync(sig); }
                catch (Exception ex) { App.Log("CALL: retry signal dispatch failed: " + ex.Message); }
            });
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
                    if (!dr.Ok || dr.ClearContent == null)
                    {
                        // Log why an old ciphertext still can't be decrypted (e.g. "unknown message
                        // index" = the shared key starts later than this event, so it's permanently
                        // undecryptable; "no session" = key still missing). The blob is kept for a
                        // possible future retry.
                        App.Log("CRYPTO: retry decrypt " + kv.Key + " not ok: " + (dr.FailureReason ?? "?"));
                        continue;
                    }
                    {
                        // A call event whose key arrived late: route it to the CallService so a still
                        // in-progress call rings (OnRemoteInvite drops it if the invite is >60s old),
                        // then drop the ciphertext placeholder so it stops rendering as a blob.
                        if (!string.IsNullOrEmpty(dr.ClearType) && dr.ClearType.StartsWith("m.call."))
                        {
                            var row = _db.GetMessageById(kv.Key);
                            string sender = row != null ? row.Sender : null;
                            long ts = row != null ? row.Timestamp : 0;
                            App.Log("CALL: retry-decrypted " + dr.ClearType + " from " + (sender ?? "?") +
                                    " ts=" + ts + " -> routing");
                            RouteDecryptedCallSignal(roomId, dr.ClearType, sender, ts, dr.ClearContent);
                            _db.DeleteMessage(kv.Key);
                            any = true;
                            continue;
                        }

                        // A late-arriving MatrixRTC ring from history: it's already stale (won't
                        // ring), so just record the timeline tile and drop the ciphertext blob.
                        if (!string.IsNullOrEmpty(dr.ClearType) && SyncProcessor.IsRtcNotificationType(dr.ClearType))
                        {
                            var row = _db.GetMessageById(kv.Key);
                            string sender = row != null ? row.Sender : null;
                            long ts = row != null ? row.Timestamp : 0;
                            var n = SyncProcessor.ParseRtcNotification(roomId, kv.Key, sender, ts, dr.ClearContent);
                            var tile = n != null
                                ? SyncProcessor.BuildRtcMissedTile(roomId, n.ParentId ?? kv.Key, sender, ts, _settings?.UserId)
                                : null;
                            if (tile != null)
                            {
                                _db.UpsertMessage(tile);
                                // Replace the "🔒 Encrypted message" preview with the missed-call label
                                // (SetRoomPreviewIfLatest only overwrites if this is the newest event).
                                _db.SetRoomPreviewIfLatest(roomId, tile.Timestamp, tile.Body);
                            }
                            App.Log("CALL: retry-decrypted MatrixRTC notification (history) -> tile, removing blob");
                            _db.DeleteMessage(kv.Key);
                            any = true;
                            continue;
                        }

                        // Sender/ts are preserved on the existing row; pass null/0 so the stored
                        // values aren't clobbered by StoreClearEvent (it reads the row first).
                        if (StoreClearEvent(roomId, kv.Key, null, 0, dr.ClearType, dr.ClearContent))
                        {
                            any = true;
                        }
                        else
                        {
                            // Decrypted but not displayable (reaction/relation/state, or a non-legacy
                            // call format): drop the ciphertext placeholder and log the cleartype.
                            App.Log("CRYPTO: retry decrypted " + kv.Key + " type=" + (dr.ClearType ?? "?") +
                                    " not displayable -> removing blob");
                            _db.DeleteMessage(kv.Key);
                            any = true;
                        }
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
        /// placeholder. Text and images render natively; other decryptable-but-unrenderable kinds
        /// (location, file, audio, video, stickers, unknown msgtypes) are stored as a short friendly
        /// placeholder so they don't show the raw ciphertext JSON. Returns true if a row was written.
        /// </summary>
        private bool StoreClearEvent(string roomId, string eventId, string sender, long ts, string clearType, JsonObject clearContent)
        {
            if (clearContent == null) return false;

            // Resolve what to display. msgType is the row's stored type (drives bubble rendering:
            // only "m.image" shows a picture, everything else is a text bubble); body is the text.
            string msgType;
            string body;
            string mxc = null;

            if (clearType == "m.room.message")
            {
                string innerMsgType = MatrixClient.GetString(clearContent, "msgtype");
                body = MatrixClient.GetString(clearContent, "body");
                switch (innerMsgType)
                {
                    case "m.text":
                    case "m.notice":
                        msgType = innerMsgType;
                        break;
                    case "m.emote":
                        msgType = "m.notice";
                        body = "* " + (body ?? "");
                        break;
                    case "m.image":
                        msgType = "m.image";
                        mxc = MatrixClient.GetString(clearContent, "url");
                        if (string.IsNullOrEmpty(mxc))
                        {
                            // Encrypted attachment: the mxc lives in a "file" block (with key/iv/hash);
                            // persist it so the media layer can decrypt the downloaded blob.
                            var file = CryptoService.GetObj(clearContent, "file");
                            if (file != null)
                            {
                                mxc = MatrixClient.GetString(file, "url");
                                if (!string.IsNullOrEmpty(mxc)) _db.SaveAttachmentKey(mxc, file.Stringify());
                            }
                        }
                        break;
                    case "m.location":
                        msgType = "m.notice";
                        body = "\uD83D\uDCCD " + FriendlyOrDefault(body, "Location");
                        break;
                    case "m.file":
                        msgType = "m.notice";
                        body = "\uD83D\uDCCE " + FriendlyOrDefault(body, "File");
                        break;
                    case "m.audio":
                        msgType = "m.notice";
                        body = "\uD83C\uDFB5 " + FriendlyOrDefault(body, "Audio message");
                        break;
                    case "m.video":
                        msgType = "m.notice";
                        body = "\uD83C\uDFAC " + FriendlyOrDefault(body, "Video");
                        break;
                    default:
                        // Unknown msgtype: show its body if it has one, else a neutral marker.
                        msgType = "m.notice";
                        if (string.IsNullOrEmpty(body)) body = "Unsupported message";
                        break;
                }
            }
            else if (clearType == "m.sticker")
            {
                mxc = MatrixClient.GetString(clearContent, "url");
                if (!string.IsNullOrEmpty(mxc))
                {
                    msgType = "m.image";
                    body = null;
                }
                else
                {
                    msgType = "m.notice";
                    body = "\uD83D\uDDBC " + FriendlyOrDefault(MatrixClient.GetString(clearContent, "body"), "Sticker");
                }
            }
            else if (clearType == "m.location")
            {
                // MSC3488 standalone location event.
                msgType = "m.notice";
                body = "\uD83D\uDCCD " + FriendlyOrDefault(MatrixClient.GetString(clearContent, "body"), "Location");
            }
            else
            {
                // Reactions, redactions, relations, state and other non-displayable events: leave the
                // existing row untouched rather than creating noise. (Call events are routed away by
                // the callers before reaching here.)
                return false;
            }

            // Recover sender/timestamp from the existing encrypted row when not supplied (retry path).
            var existing = _db.GetMessageById(eventId);
            if (string.IsNullOrEmpty(sender) && existing != null) sender = existing.Sender;
            if (ts == 0 && existing != null) ts = existing.Timestamp;

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

            // Replace the "🔒 Encrypted message" room-list preview with the decrypted text, but only
            // when this is the latest event (so older messages decrypting later don't clobber it).
            string preview = msgType == "m.image" ? "\uD83D\uDCF7 Photo" : body;
            _db.SetRoomPreviewIfLatest(roomId, ts, preview);
            return true;
        }

        /// <summary>Returns the message body when present, otherwise a generic fallback label.</summary>
        private static string FriendlyOrDefault(string body, string fallback)
        {
            return string.IsNullOrEmpty(body) ? fallback : body;
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
            App.Log("TX-plain " + roomId + " type=m.room.message content=" + content.Stringify());
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

        /// <summary>
        /// Sends a Matrix VoIP signalling event (m.call.*), encrypting it with Megolm when the room
        /// is encrypted. Injected into the CallService so calls work in encrypted rooms: a plaintext
        /// m.call.* event in an encrypted room is rejected/ignored by other clients. Returns the
        /// event id, or throws on encryption failure (we never fall back to plaintext in an
        /// encrypted room).
        ///
        /// The CallService fires sends from several threads (the offer/answer build inside Task.Run,
        /// the ICE candidate flush timer, the native WebRTC callback thread). The outbound Megolm
        /// session is NOT thread-safe — two concurrent encrypts could reuse a ratchet index (key
        /// reuse), so we marshal every call-event encrypt onto the UI thread, the same thread the
        /// message-send path uses, which serialises all outbound Megolm access.
        /// </summary>
        private Task<string> SendCallEventAsync(string roomId, string eventType, JsonObject content)
        {
            if (Dispatcher.HasThreadAccess)
                return SendCallEventCoreAsync(roomId, eventType, content);

            var tcs = new TaskCompletionSource<string>();
            var _ = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                try { tcs.SetResult(await SendCallEventCoreAsync(roomId, eventType, content)); }
                catch (Exception ex) { tcs.SetException(ex); }
            });
            return tcs.Task;
        }

        private async Task<string> SendCallEventCoreAsync(string roomId, string eventType, JsonObject content)
        {
            if (RoomNeedsEncryption(roomId))
            {
                await ShareRoomKeysAsync(roomId);
                var encrypted = _crypto.EncryptRoomEvent(roomId, eventType, content);
                if (encrypted == null) throw new Exception("Encryption failed");
                App.Log("CALL: sent " + eventType + " ENCRYPTED (m.room.encrypted)");
                return await _client.SendEventAsync(roomId, "m.room.encrypted", encrypted);
            }

            App.Log("CALL: sent " + eventType + " PLAINTEXT (room not encrypted)");
            return await _client.SendEventAsync(roomId, eventType, content);
        }
    }
}