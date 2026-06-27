using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using UniMatrix.Crypto;
using UniMatrix.Data;

namespace UniMatrix.Services
{
    /// <summary>
    /// The result of attempting to decrypt a room event.
    /// </summary>
    internal class DecryptResult
    {
        public bool Ok;
        public string ClearType;       // inner event type, e.g. "m.room.message"
        public JsonObject ClearContent; // inner content
        public string FailureReason;    // set when Ok == false
    }

    /// <summary>
    /// Orchestrates Matrix end-to-end encryption: the Olm account and device keys, one-time-key
    /// upkeep, device-list tracking, Olm to-device key sharing, and Megolm room
    /// encryption/decryption. The actual cryptography is provided by libolm via the wrappers in
    /// OlmWrappers.cs and only compiled in on ARM (the CRYPTO define + harvested olm.dll). On
    /// other architectures the service reports Available == false and all operations are no-ops,
    /// so the rest of the app still compiles and runs unencrypted.
    /// </summary>
    internal class CryptoService
    {
        private const string AlgOlm = "m.olm.v1.curve25519-aes-sha2";
        private const string AlgMegolm = "m.megolm.v1.aes-sha2";

        private readonly MatrixDatabase _db;
        private readonly MatrixClient _client;
        private readonly PreferencesService _prefs;

        private string _userId;
        private string _deviceId;
        private string _curve25519;
        private string _ed25519;
        private bool _available;

        /// <summary>Raised (on the calling thread) when a new inbound Megolm session is stored, so
        /// the key-backup service can upload it. May be null.</summary>
        public Action<StoredInboundGroupSession> OnInboundSessionAdded;

        /// <summary>Raised when an m.secret.send to-device message arrives (request_id, secret).</summary>
        public Action<string, string> OnSecretReceived;

        public CryptoService(MatrixDatabase db, MatrixClient client, PreferencesService prefs)
        {
            _db = db;
            _client = client;
            _prefs = prefs;
        }

        /// <summary>True once an Olm account is loaded and the platform supports encryption.</summary>
        public bool Available { get { return _available; } }

        public string DeviceId { get { return _deviceId; } }
        public string IdentityCurve25519 { get { return _curve25519; } }
        public string IdentityEd25519 { get { return _ed25519; } }

        public bool IsRoomEncrypted(string roomId) { return _db.IsRoomEncrypted(roomId); }

#if CRYPTO
        private OlmAccount _account;
        private byte[] _pickleKey;
        private readonly object _accountGate = new object();

        // ---- Initialization ----

        public async Task InitializeAsync()
        {
            try
            {
                _userId = _client.UserId;
                _deviceId = _prefs.DeviceId;
                if (string.IsNullOrEmpty(_userId) || string.IsNullOrEmpty(_deviceId))
                {
                    App.Log("CRYPTO: no user/device id; encryption disabled");
                    return;
                }

                string pkB64 = _prefs.GetOrCreatePickleKey();
                _pickleKey = Convert.FromBase64String(pkB64);

                string pickle = _db.GetAccountPickle();
                bool freshAccount = false;
                if (!string.IsNullOrEmpty(pickle))
                {
                    _account = OlmAccount.Unpickle(pickle, _pickleKey);
                }
                else
                {
                    _account = OlmAccount.Create();
                    freshAccount = true;
                }

                LoadIdentityKeys();
                if (freshAccount)
                {
                    _db.SaveAccount(_account.Pickle(_pickleKey), _deviceId, _ed25519, _curve25519);
                }

                _available = true;
                App.Log("CRYPTO: ready device=" + _deviceId + " curve=" + Trunc(_curve25519));

                if (freshAccount)
                {
                    await UploadDeviceKeysAsync();
                    await EnsureOneTimeKeysAsync(0);
                }
            }
            catch (Exception ex)
            {
                _available = false;
                App.Log("CRYPTO: init failed: " + ex.Message);
            }
        }

        private void LoadIdentityKeys()
        {
            var keys = JsonObject.Parse(_account.IdentityKeysJson());
            _curve25519 = MatrixClient.GetString(keys, "curve25519");
            _ed25519 = MatrixClient.GetString(keys, "ed25519");
        }

        private void PersistAccount()
        {
            lock (_accountGate) { _db.UpdateAccountPickle(_account.Pickle(_pickleKey)); }
        }

        // ---- Signing ----

        /// <summary>Signs an object's canonical form (minus signatures/unsigned) with our Ed25519.</summary>
        private string SignCanonical(JsonObject obj)
        {
            var copy = Clone(obj);
            if (copy.ContainsKey("signatures")) copy.Remove("signatures");
            if (copy.ContainsKey("unsigned")) copy.Remove("unsigned");
            string canon = CanonicalJson.Serialize(copy);
            return _account.Sign(canon);
        }

        private void AddOurSignature(JsonObject obj)
        {
            string sig = SignCanonical(obj);
            var bySig = new JsonObject { ["ed25519:" + _deviceId] = JsonValue.CreateStringValue(sig) };
            obj["signatures"] = new JsonObject { [_userId] = bySig };
        }

        // ---- Device keys + one-time keys ----

        private JsonObject BuildDeviceKeys()
        {
            var algorithms = new JsonArray();
            algorithms.Add(JsonValue.CreateStringValue(AlgOlm));
            algorithms.Add(JsonValue.CreateStringValue(AlgMegolm));

            var keys = new JsonObject
            {
                ["curve25519:" + _deviceId] = JsonValue.CreateStringValue(_curve25519),
                ["ed25519:" + _deviceId] = JsonValue.CreateStringValue(_ed25519)
            };

            var deviceKeys = new JsonObject
            {
                ["user_id"] = JsonValue.CreateStringValue(_userId),
                ["device_id"] = JsonValue.CreateStringValue(_deviceId),
                ["algorithms"] = algorithms,
                ["keys"] = keys
            };
            AddOurSignature(deviceKeys);
            return deviceKeys;
        }

        private async Task UploadDeviceKeysAsync()
        {
            try
            {
                await _client.KeysUploadAsync(BuildDeviceKeys(), null);
                App.Log("CRYPTO: device keys uploaded");
            }
            catch (Exception ex) { App.Log("CRYPTO: device key upload failed: " + ex.Message); }
        }

        /// <summary>Tops up published one-time keys to half the device maximum.</summary>
        public async Task EnsureOneTimeKeysAsync(int serverCount)
        {
            if (!_available) return;
            try
            {
                int max = _account.MaxOneTimeKeys();
                int target = max / 2;
                int need = target - serverCount;
                if (need <= 0) return;

                _account.GenerateOneTimeKeys(need);
                var otks = JsonObject.Parse(_account.OneTimeKeysJson());
                var curve = (otks.ContainsKey("curve25519") && otks["curve25519"].ValueType == JsonValueType.Object)
                    ? otks.GetNamedObject("curve25519") : new JsonObject();

                var signed = new JsonObject();
                foreach (var keyId in curve.Keys)
                {
                    string keyVal = MatrixClient.GetString(curve, keyId);
                    var entry = new JsonObject { ["key"] = JsonValue.CreateStringValue(keyVal) };
                    AddOurSignature(entry);
                    signed["signed_curve25519:" + keyId] = entry;
                }

                await _client.KeysUploadAsync(null, signed);
                _account.MarkKeysAsPublished();
                PersistAccount();
                App.Log("CRYPTO: uploaded " + need + " one-time keys");
            }
            catch (Exception ex) { App.Log("CRYPTO: OTK upload failed: " + ex.Message); }
        }

        // ---- Device-list tracking ----

        public void MarkUserDirty(string userId)
        {
            if (!_available || string.IsNullOrEmpty(userId)) return;
            _db.SetTrackedUser(userId, true);
        }

        public void MarkUsersDirty(IEnumerable<string> userIds)
        {
            if (!_available || userIds == null) return;
            foreach (var u in userIds) MarkUserDirty(u);
        }

        /// <summary>Downloads + verifies device keys for the given users (TOFU trust on first sight).</summary>
        public async Task UpdateDeviceKeysAsync(IEnumerable<string> userIds)
        {
            if (!_available) return;
            var list = new List<string>();
            foreach (var u in userIds) if (!string.IsNullOrEmpty(u)) list.Add(u);
            if (list.Count == 0) return;

            try
            {
                var resp = await _client.KeysQueryAsync(list);
                if (resp == null) return;
                var deviceKeys = (resp.ContainsKey("device_keys") && resp["device_keys"].ValueType == JsonValueType.Object)
                    ? resp.GetNamedObject("device_keys") : null;
                if (deviceKeys == null) return;

                using (var util = new OlmUtility())
                {
                    foreach (var uid in deviceKeys.Keys)
                    {
                        var devices = deviceKeys.GetNamedObject(uid);
                        foreach (var devId in devices.Keys)
                        {
                            try { VerifyAndStoreDevice(util, uid, devId, devices.GetNamedObject(devId)); }
                            catch (Exception ex) { App.Log("CRYPTO: device " + uid + "/" + devId + " rejected: " + ex.Message); }
                        }
                        _db.SetTrackedUser(uid, false);
                    }
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: keys/query failed: " + ex.Message); }
        }

        private void VerifyAndStoreDevice(OlmUtility util, string userId, string deviceId, JsonObject dk)
        {
            string declaredUser = MatrixClient.GetString(dk, "user_id");
            string declaredDev = MatrixClient.GetString(dk, "device_id");
            if (declaredUser != userId || declaredDev != deviceId) throw new Exception("id mismatch");

            var keys = dk.GetNamedObject("keys");
            string curve = MatrixClient.GetString(keys, "curve25519:" + deviceId);
            string ed = MatrixClient.GetString(keys, "ed25519:" + deviceId);
            if (string.IsNullOrEmpty(curve) || string.IsNullOrEmpty(ed)) throw new Exception("missing keys");

            var sigs = dk.GetNamedObject("signatures").GetNamedObject(userId);
            string sig = MatrixClient.GetString(sigs, "ed25519:" + deviceId);
            if (string.IsNullOrEmpty(sig)) throw new Exception("no self-signature");

            var copy = Clone(dk);
            copy.Remove("signatures");
            if (copy.ContainsKey("unsigned")) copy.Remove("unsigned");
            if (!util.Verify(ed, CanonicalJson.Serialize(copy), sig)) throw new Exception("bad signature");

            // TOFU: reject a changed Ed25519 for a device we already trust.
            var existing = _db.GetDevice(userId, deviceId);
            if (existing != null && !string.IsNullOrEmpty(existing.Ed25519) && existing.Ed25519 != ed)
                throw new Exception("ed25519 changed (possible MITM)");

            _db.SaveDevice(new StoredDevice
            {
                UserId = userId,
                DeviceId = deviceId,
                Curve25519 = curve,
                Ed25519 = ed,
                Json = dk.Stringify(),
                Trust = 0
            });
        }

        // ---- Inbound: to-device handling ----

        public async Task<HashSet<string>> HandleToDeviceEventsAsync(IList<JsonObject> events)
        {
            var newKeyRooms = new HashSet<string>();
            if (!_available || events == null) return newKeyRooms;
            bool accountTouched = false;

            foreach (var ev in events)
            {
                try
                {
                    string type = MatrixClient.GetString(ev, "type");
                    string sender = MatrixClient.GetString(ev, "sender");
                    var content = GetObj(ev, "content");
                    if (content == null) continue;

                    if (type == "m.room.encrypted")
                    {
                        if (HandleOlmEvent(sender, content, newKeyRooms)) accountTouched = true;
                    }
                }
                catch (Exception ex) { App.Log("CRYPTO: to-device error: " + ex.Message); }
            }

            if (accountTouched) PersistAccount();
            await Task.FromResult(0);
            return newKeyRooms;
        }

        /// <summary>Decrypts an Olm to-device m.room.encrypted event and dispatches the inner event.
        /// Returns true if the Olm account was mutated (a one-time key was consumed).</summary>
        private bool HandleOlmEvent(string sender, JsonObject content, HashSet<string> newKeyRooms)
        {
            string algorithm = MatrixClient.GetString(content, "algorithm");
            if (algorithm != AlgOlm) return false;

            string senderKey = MatrixClient.GetString(content, "sender_key");
            var ciphertext = GetObj(content, "ciphertext");
            if (ciphertext == null || string.IsNullOrEmpty(senderKey)) return false;

            // Find the message addressed to our Curve25519 key.
            var ourMsg = (ciphertext.ContainsKey(_curve25519) && ciphertext[_curve25519].ValueType == JsonValueType.Object)
                ? ciphertext.GetNamedObject(_curve25519) : null;
            if (ourMsg == null) return false;

            int msgType = (int)MatrixClient.GetNumber(ourMsg, "type");
            string body = MatrixClient.GetString(ourMsg, "body");
            if (string.IsNullOrEmpty(body)) return false;

            bool accountTouched;
            string plaintext = OlmDecrypt(senderKey, msgType, body, out accountTouched);
            if (plaintext == null) { App.Log("CRYPTO: olm decrypt failed from " + Trunc(senderKey)); return accountTouched; }

            JsonObject inner;
            if (!JsonObject.TryParse(plaintext, out inner)) return accountTouched;

            // Validate the decrypted event is really for us.
            string recipient = MatrixClient.GetString(inner, "recipient");
            var recipientKeys = GetObj(inner, "recipient_keys");
            string recipEd = recipientKeys != null ? MatrixClient.GetString(recipientKeys, "ed25519") : null;
            if (recipient != _userId || recipEd != _ed25519)
            {
                App.Log("CRYPTO: olm event recipient mismatch");
                return accountTouched;
            }

            string innerType = MatrixClient.GetString(inner, "type");
            var innerContent = GetObj(inner, "content");
            var senderKeysObj = GetObj(inner, "keys");
            string senderEd = senderKeysObj != null ? MatrixClient.GetString(senderKeysObj, "ed25519") : null;
            if (innerContent == null) return accountTouched;

            DispatchToDevice(innerType, sender, senderKey, senderEd, innerContent, newKeyRooms);
            return accountTouched;
        }

        private void DispatchToDevice(string innerType, string sender, string senderKey, string senderEd, JsonObject content, HashSet<string> newKeyRooms)
        {
            if (innerType == "m.room_key")
            {
                string alg = MatrixClient.GetString(content, "algorithm");
                if (alg != AlgMegolm) return;
                string roomId = MatrixClient.GetString(content, "room_id");
                string sessionId = MatrixClient.GetString(content, "session_id");
                string sessionKey = MatrixClient.GetString(content, "session_key");
                if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(sessionKey)) return;

                if (_db.HasInboundGroupSession(roomId, senderKey, sessionId)) return;
                using (var igs = OlmInboundGroupSession.Create(sessionKey))
                {
                    var stored = new StoredInboundGroupSession
                    {
                        RoomId = roomId,
                        SenderKey = senderKey,
                        SessionId = string.IsNullOrEmpty(sessionId) ? igs.SessionId() : sessionId,
                        Pickle = igs.Pickle(_pickleKey),
                        Ed25519 = senderEd,
                        Forwarded = false
                    };
                    _db.SaveInboundGroupSession(stored);
                    if (newKeyRooms != null) newKeyRooms.Add(roomId);
                    App.Log("CRYPTO: stored inbound megolm session room=" + roomId + " sid=" + Trunc(stored.SessionId));
                    if (OnInboundSessionAdded != null) { try { OnInboundSessionAdded(stored); } catch { } }
                }
            }
            else if (innerType == "m.forwarded_room_key")
            {
                string alg = MatrixClient.GetString(content, "algorithm");
                if (alg != AlgMegolm) return;
                string roomId = MatrixClient.GetString(content, "room_id");
                string sessionKey = MatrixClient.GetString(content, "session_key");
                string fwdSenderKey = MatrixClient.GetString(content, "sender_key");
                string claimedEd = MatrixClient.GetString(content, "sender_claimed_ed25519_key");
                if (string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(sessionKey)) return;

                using (var igs = OlmInboundGroupSession.Import(sessionKey))
                {
                    var stored = new StoredInboundGroupSession
                    {
                        RoomId = roomId,
                        SenderKey = string.IsNullOrEmpty(fwdSenderKey) ? senderKey : fwdSenderKey,
                        SessionId = igs.SessionId(),
                        Pickle = igs.Pickle(_pickleKey),
                        Ed25519 = claimedEd,
                        Forwarded = true
                    };
                    if (_db.HasInboundGroupSession(stored.RoomId, stored.SenderKey, stored.SessionId)) return;
                    _db.SaveInboundGroupSession(stored);
                    if (newKeyRooms != null) newKeyRooms.Add(stored.RoomId);
                    if (OnInboundSessionAdded != null) { try { OnInboundSessionAdded(stored); } catch { } }
                }
            }
            else if (innerType == "m.secret.send")
            {
                string requestId = MatrixClient.GetString(content, "request_id");
                string secret = MatrixClient.GetString(content, "secret");
                if (OnSecretReceived != null) { try { OnSecretReceived(requestId, secret); } catch { } }
            }
        }

        /// <summary>Decrypts an Olm ciphertext from a peer, trying existing sessions then (for a
        /// prekey message) creating a new inbound session. Returns plaintext or null.</summary>
        private string OlmDecrypt(string senderKey, int msgType, string body, out bool accountTouched)
        {
            accountTouched = false;

            // Try each stored session first.
            foreach (var s in _db.GetOlmSessions(senderKey))
            {
                try
                {
                    using (var session = OlmSession.Unpickle(s.Pickle, _pickleKey))
                    {
                        if (msgType == 0 && !session.MatchesInbound(senderKey, body)) continue;
                        string pt = session.Decrypt(msgType, body);
                        _db.SaveOlmSession(senderKey, session.SessionId(), session.Pickle(_pickleKey), Now());
                        return pt;
                    }
                }
                catch { /* try next session */ }
            }

            // No existing session worked. For a prekey (type 0) message, create one.
            if (msgType == 0)
            {
                try
                {
                    using (var inbound = OlmSession.CreateInbound(_account, senderKey, body))
                    {
                        OlmNative.olm_remove_one_time_keys(_account.Handle, inbound.Handle);
                        string pt = inbound.Decrypt(msgType, body);
                        _db.SaveOlmSession(senderKey, inbound.SessionId(), inbound.Pickle(_pickleKey), Now());
                        accountTouched = true;
                        return pt;
                    }
                }
                catch (Exception ex) { App.Log("CRYPTO: create inbound olm failed: " + ex.Message); }
            }
            return null;
        }

        // ---- Inbound: Megolm room event decryption ----

        public DecryptResult DecryptRoomEvent(string roomId, JsonObject content)
        {
            var result = new DecryptResult();
            if (!_available) { result.FailureReason = "crypto unavailable"; return result; }

            try
            {
                string algorithm = MatrixClient.GetString(content, "algorithm");
                if (algorithm != AlgMegolm) { result.FailureReason = "unsupported algorithm"; return result; }

                string senderKey = MatrixClient.GetString(content, "sender_key");
                string sessionId = MatrixClient.GetString(content, "session_id");
                string ciphertext = MatrixClient.GetString(content, "ciphertext");
                if (string.IsNullOrEmpty(ciphertext) || string.IsNullOrEmpty(sessionId))
                { result.FailureReason = "missing ciphertext"; return result; }

                var stored = _db.GetInboundGroupSession(roomId, senderKey, sessionId);
                if (stored == null) { result.FailureReason = "no session"; return result; }

                using (var igs = OlmInboundGroupSession.Unpickle(stored.Pickle, _pickleKey))
                {
                    uint index;
                    string plaintext = igs.Decrypt(ciphertext, out index);
                    JsonObject inner;
                    if (!JsonObject.TryParse(plaintext, out inner)) { result.FailureReason = "bad plaintext"; return result; }

                    string innerRoom = MatrixClient.GetString(inner, "room_id");
                    if (!string.IsNullOrEmpty(innerRoom) && innerRoom != roomId)
                    { result.FailureReason = "room mismatch"; return result; }

                    result.Ok = true;
                    result.ClearType = MatrixClient.GetString(inner, "type");
                    result.ClearContent = GetObj(inner, "content");
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.FailureReason = ex.Message;
                return result;
            }
        }

        // ---- Outbound: Megolm room encryption ----

        /// <summary>Encrypts a room event into an m.room.encrypted content payload.</summary>
        public JsonObject EncryptRoomEvent(string roomId, string eventType, JsonObject content)
        {
            if (!_available) return null;
            try
            {
                var outbound = LoadOrCreateOutbound(roomId);
                using (outbound.Session)
                {
                    var payload = new JsonObject
                    {
                        ["type"] = JsonValue.CreateStringValue(eventType),
                        ["content"] = content,
                        ["room_id"] = JsonValue.CreateStringValue(roomId)
                    };
                    string ciphertext = outbound.Session.Encrypt(payload.Stringify());

                    var encrypted = new JsonObject
                    {
                        ["algorithm"] = JsonValue.CreateStringValue(AlgMegolm),
                        ["sender_key"] = JsonValue.CreateStringValue(_curve25519),
                        ["ciphertext"] = JsonValue.CreateStringValue(ciphertext),
                        ["session_id"] = JsonValue.CreateStringValue(outbound.Session.SessionId()),
                        ["device_id"] = JsonValue.CreateStringValue(_deviceId)
                    };

                    // Persist updated ratchet + message count.
                    outbound.Record.Pickle = outbound.Session.Pickle(_pickleKey);
                    outbound.Record.MsgCount += 1;
                    _db.SaveOutboundGroupSession(outbound.Record);
                    return encrypted;
                }
            }
            catch (Exception ex)
            {
                App.Log("CRYPTO: encrypt room event failed: " + ex.Message);
                return null;
            }
        }

        private class OutboundHandle
        {
            public OlmOutboundGroupSession Session;
            public StoredOutboundGroupSession Record;
        }

        private OutboundHandle LoadOrCreateOutbound(string roomId)
        {
            var rec = _db.GetOutboundGroupSession(roomId);
            if (rec != null && !string.IsNullOrEmpty(rec.Pickle))
            {
                return new OutboundHandle { Session = OlmOutboundGroupSession.Unpickle(rec.Pickle, _pickleKey), Record = rec };
            }

            var session = OlmOutboundGroupSession.Create();
            var newRec = new StoredOutboundGroupSession
            {
                RoomId = roomId,
                Pickle = session.Pickle(_pickleKey),
                SessionId = session.SessionId(),
                CreatedTs = Now(),
                MsgCount = 0,
                SharedTo = "[]"
            };
            _db.SaveOutboundGroupSession(newRec);

            // Store our own inbound copy so we can read our own messages.
            using (var igs = OlmInboundGroupSession.Create(session.SessionKey()))
            {
                var stored = new StoredInboundGroupSession
                {
                    RoomId = roomId,
                    SenderKey = _curve25519,
                    SessionId = session.SessionId(),
                    Pickle = igs.Pickle(_pickleKey),
                    Ed25519 = _ed25519,
                    Forwarded = false
                };
                _db.SaveInboundGroupSession(stored);
                if (OnInboundSessionAdded != null) { try { OnInboundSessionAdded(stored); } catch { } }
            }
            return new OutboundHandle { Session = session, Record = newRec };
        }

        /// <summary>Ensures the current outbound Megolm session key has been shared (via Olm
        /// to-device) with every device of every member of the room.</summary>
        public async Task EnsureRoomKeysSharedAsync(string roomId, IEnumerable<string> memberUserIds)
        {
            if (!_available) return;
            try
            {
                // Make sure an outbound session exists and grab its key/id.
                string sessionId, sessionKey;
                var rec = _db.GetOutboundGroupSession(roomId);
                if (rec == null || string.IsNullOrEmpty(rec.Pickle))
                {
                    var handle = LoadOrCreateOutbound(roomId);
                    using (handle.Session) { sessionId = handle.Session.SessionId(); sessionKey = handle.Session.SessionKey(); }
                    rec = handle.Record;
                }
                else
                {
                    using (var s = OlmOutboundGroupSession.Unpickle(rec.Pickle, _pickleKey))
                    { sessionId = s.SessionId(); sessionKey = s.SessionKey(); }
                }

                var sharedTo = ParseSharedTo(rec.SharedTo);

                // Refresh device lists for any dirty/untracked members.
                var toQuery = new List<string>();
                foreach (var u in memberUserIds)
                {
                    if (string.IsNullOrEmpty(u)) continue;
                    if (!_db.IsUserTracked(u) || _db.GetDevices(u).Count == 0) toQuery.Add(u);
                }
                if (toQuery.Count > 0) await UpdateDeviceKeysAsync(toQuery);

                // Determine which (user, device) still need the key.
                var targets = new List<StoredDevice>();
                foreach (var u in memberUserIds)
                {
                    if (string.IsNullOrEmpty(u)) continue;
                    foreach (var dev in _db.GetDevices(u))
                    {
                        if (dev.UserId == _userId && dev.DeviceId == _deviceId) continue; // skip self
                        if (string.IsNullOrEmpty(dev.Curve25519)) continue;
                        string tag = dev.UserId + "|" + dev.DeviceId;
                        if (sharedTo.Contains(tag)) continue;
                        targets.Add(dev);
                    }
                }
                if (targets.Count == 0) return;

                await EnsureOlmSessionsAsync(targets);

                // Build the m.room_key payload and Olm-encrypt it per device.
                var roomKeyContent = new JsonObject
                {
                    ["algorithm"] = JsonValue.CreateStringValue(AlgMegolm),
                    ["room_id"] = JsonValue.CreateStringValue(roomId),
                    ["session_id"] = JsonValue.CreateStringValue(sessionId),
                    ["session_key"] = JsonValue.CreateStringValue(sessionKey)
                };

                var messages = new JsonObject();
                bool accountTouched = false;
                foreach (var dev in targets)
                {
                    var encrypted = OlmEncryptForDevice(dev, "m.room_key", roomKeyContent, ref accountTouched);
                    if (encrypted == null) continue;
                    JsonObject perUser = (messages.ContainsKey(dev.UserId) && messages[dev.UserId].ValueType == JsonValueType.Object)
                        ? messages.GetNamedObject(dev.UserId) : new JsonObject();
                    perUser[dev.DeviceId] = encrypted;
                    messages[dev.UserId] = perUser;
                    sharedTo.Add(dev.UserId + "|" + dev.DeviceId);
                }

                if (messages.Count > 0)
                {
                    await _client.SendToDeviceAsync("m.room.encrypted", messages);
                    rec.SharedTo = SerializeSharedTo(sharedTo);
                    _db.SaveOutboundGroupSession(rec);
                    if (accountTouched) PersistAccount();
                    App.Log("CRYPTO: shared room key for " + roomId + " to " + targets.Count + " device(s)");
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: share room key failed: " + ex.Message); }
        }

        /// <summary>Claims one-time keys and creates Olm sessions for any target device we don't
        /// already have a session with.</summary>
        private async Task EnsureOlmSessionsAsync(List<StoredDevice> devices)
        {
            var claim = new JsonObject();
            var pending = new List<StoredDevice>();
            foreach (var dev in devices)
            {
                if (_db.GetOlmSessions(dev.Curve25519).Count > 0) continue;
                pending.Add(dev);
                JsonObject perUser = (claim.ContainsKey(dev.UserId) && claim[dev.UserId].ValueType == JsonValueType.Object)
                    ? claim.GetNamedObject(dev.UserId) : new JsonObject();
                perUser[dev.DeviceId] = JsonValue.CreateStringValue("signed_curve25519");
                claim[dev.UserId] = perUser;
            }
            if (pending.Count == 0) return;

            var resp = await _client.KeysClaimAsync(claim);
            var otks = (resp != null && resp.ContainsKey("one_time_keys") && resp["one_time_keys"].ValueType == JsonValueType.Object)
                ? resp.GetNamedObject("one_time_keys") : null;
            if (otks == null) return;

            using (var util = new OlmUtility())
            {
                foreach (var dev in pending)
                {
                    try
                    {
                        if (!otks.ContainsKey(dev.UserId)) continue;
                        var byDevice = otks.GetNamedObject(dev.UserId);
                        if (!byDevice.ContainsKey(dev.DeviceId)) continue;
                        var keyObj = byDevice.GetNamedObject(dev.DeviceId);

                        // The single entry is "signed_curve25519:<id>": { key, signatures }.
                        foreach (var entryKey in keyObj.Keys)
                        {
                            var entry = keyObj.GetNamedObject(entryKey);
                            string otk = MatrixClient.GetString(entry, "key");
                            if (string.IsNullOrEmpty(otk)) continue;

                            // Verify the OTK signature against the device's Ed25519.
                            var copy = Clone(entry);
                            copy.Remove("signatures");
                            if (copy.ContainsKey("unsigned")) copy.Remove("unsigned");
                            var sigs = entry.GetNamedObject("signatures").GetNamedObject(dev.UserId);
                            string sig = MatrixClient.GetString(sigs, "ed25519:" + dev.DeviceId);
                            if (string.IsNullOrEmpty(sig) || !util.Verify(dev.Ed25519, CanonicalJson.Serialize(copy), sig))
                            { App.Log("CRYPTO: bad OTK signature for " + dev.UserId + "/" + dev.DeviceId); break; }

                            using (var session = OlmSession.CreateOutbound(_account, dev.Curve25519, otk))
                            {
                                _db.SaveOlmSession(dev.Curve25519, session.SessionId(), session.Pickle(_pickleKey), Now());
                            }
                            break;
                        }
                    }
                    catch (Exception ex) { App.Log("CRYPTO: claim/session for " + dev.UserId + "/" + dev.DeviceId + " failed: " + ex.Message); }
                }
            }
        }

        /// <summary>Wraps an event in an Olm to-device m.room.encrypted payload for one device.</summary>
        private JsonObject OlmEncryptForDevice(StoredDevice dev, string eventType, JsonObject content, ref bool accountTouched)
        {
            var sessions = _db.GetOlmSessions(dev.Curve25519);
            if (sessions.Count == 0) return null;

            var payload = new JsonObject
            {
                ["type"] = JsonValue.CreateStringValue(eventType),
                ["content"] = content,
                ["sender"] = JsonValue.CreateStringValue(_userId),
                ["sender_device"] = JsonValue.CreateStringValue(_deviceId),
                ["keys"] = new JsonObject { ["ed25519"] = JsonValue.CreateStringValue(_ed25519) },
                ["recipient"] = JsonValue.CreateStringValue(dev.UserId),
                ["recipient_keys"] = new JsonObject { ["ed25519"] = JsonValue.CreateStringValue(dev.Ed25519) }
            };

            try
            {
                using (var session = OlmSession.Unpickle(sessions[0].Pickle, _pickleKey))
                {
                    int type = session.EncryptMessageType();
                    string body = session.Encrypt(payload.Stringify());
                    _db.SaveOlmSession(dev.Curve25519, session.SessionId(), session.Pickle(_pickleKey), Now());

                    var msg = new JsonObject { ["type"] = JsonValue.CreateNumberValue(type), ["body"] = JsonValue.CreateStringValue(body) };
                    return new JsonObject
                    {
                        ["algorithm"] = JsonValue.CreateStringValue(AlgOlm),
                        ["sender_key"] = JsonValue.CreateStringValue(_curve25519),
                        ["ciphertext"] = new JsonObject { [dev.Curve25519] = msg }
                    };
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: olm encrypt for device failed: " + ex.Message); return null; }
        }

        /// <summary>Invalidates the outbound session for a room (e.g. on a membership change), so a
        /// fresh key is created and re-shared on the next send.</summary>
        public void RotateOutbound(string roomId)
        {
            if (!_available) return;
            _db.DeleteOutboundGroupSession(roomId);
        }

        // ---- Key-backup support (used by KeyBackupService) ----

        /// <summary>Adds our Ed25519 signature to an object in place (canonical, minus
        /// signatures/unsigned). Used to sign key-backup auth_data.</summary>
        public void SignObject(JsonObject obj)
        {
            if (!_available || obj == null) return;
            lock (_accountGate) { AddOurSignature(obj); }
        }

        /// <summary>Exports a stored inbound Megolm session's key at its first known index, for
        /// upload to the server-side backup. Returns null if it can't be unpickled.</summary>
        public string ExportInboundSession(StoredInboundGroupSession s, out uint firstIndex)
        {
            firstIndex = 0;
            if (!_available || s == null || string.IsNullOrEmpty(s.Pickle)) return null;
            try
            {
                using (var igs = OlmInboundGroupSession.Unpickle(s.Pickle, _pickleKey))
                {
                    firstIndex = igs.FirstKnownIndex();
                    return igs.Export(firstIndex);
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: ExportInboundSession failed: " + ex.Message); return null; }
        }

        /// <summary>Imports a Megolm session (from backup or forwarding) and stores it. Returns true
        /// if a new session was stored (false if we already had it or import failed).</summary>
        public bool ImportInboundSession(string roomId, string sessionKey, string senderKey, string ed25519, bool forwarded)
        {
            if (!_available || string.IsNullOrEmpty(roomId) || string.IsNullOrEmpty(sessionKey)) return false;
            try
            {
                using (var igs = OlmInboundGroupSession.Import(sessionKey))
                {
                    string sessionId = igs.SessionId();
                    if (_db.HasInboundGroupSession(roomId, senderKey, sessionId)) return false;
                    var stored = new StoredInboundGroupSession
                    {
                        RoomId = roomId,
                        SenderKey = senderKey,
                        SessionId = sessionId,
                        Pickle = igs.Pickle(_pickleKey),
                        Ed25519 = ed25519,
                        Forwarded = forwarded
                    };
                    _db.SaveInboundGroupSession(stored);
                    return true;
                }
            }
            catch (Exception ex) { App.Log("CRYPTO: ImportInboundSession failed: " + ex.Message); return false; }
        }

        // ---- Smoke test ----

        /// <summary>A self-contained sanity check used during bring-up. Returns a human-readable
        /// status string; logs detail to the debug log.</summary>
        public string RunSelfTest()
        {
            try
            {
                using (var acc = OlmAccount.Create())
                {
                    var ik = JsonObject.Parse(acc.IdentityKeysJson());
                    string ed = MatrixClient.GetString(ik, "ed25519");
                    string sig = acc.Sign("hello");
                    using (var util = new OlmUtility())
                    {
                        if (!util.Verify(ed, "hello", sig)) return "FAIL: sign/verify";
                    }
                    // Megolm round-trip.
                    using (var outb = OlmOutboundGroupSession.Create())
                    using (var inb = OlmInboundGroupSession.Create(outb.SessionKey()))
                    {
                        string ct = outb.Encrypt("secret message");
                        uint idx;
                        string pt = inb.Decrypt(ct, out idx);
                        if (pt != "secret message") return "FAIL: megolm roundtrip";
                    }
                }
                return "OK: olm self-test passed";
            }
            catch (Exception ex) { return "FAIL: " + ex.Message; }
        }

        // ---- helpers ----

        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private static HashSet<string> ParseSharedTo(string json)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrEmpty(json)) return set;
            try
            {
                var arr = JsonArray.Parse(json);
                foreach (var v in arr) if (v.ValueType == JsonValueType.String) set.Add(v.GetString());
            }
            catch { }
            return set;
        }

        private static string SerializeSharedTo(HashSet<string> set)
        {
            var arr = new JsonArray();
            foreach (var s in set) arr.Add(JsonValue.CreateStringValue(s));
            return arr.Stringify();
        }

        private static string Trunc(string s) { return string.IsNullOrEmpty(s) ? "" : (s.Length > 8 ? s.Substring(0, 8) : s); }
#else
        // Non-ARM builds: encryption is unavailable; everything is a safe no-op so the rest of the
        // app compiles and runs against unencrypted rooms only.
        public Task InitializeAsync() { return Task.FromResult(0); }
        public Task EnsureOneTimeKeysAsync(int serverCount) { return Task.FromResult(0); }
        public void MarkUserDirty(string userId) { }
        public void MarkUsersDirty(IEnumerable<string> userIds) { }
        public Task UpdateDeviceKeysAsync(IEnumerable<string> userIds) { return Task.FromResult(0); }
        public Task<HashSet<string>> HandleToDeviceEventsAsync(IList<JsonObject> events) { return Task.FromResult(new HashSet<string>()); }
        public DecryptResult DecryptRoomEvent(string roomId, JsonObject content)
        { return new DecryptResult { FailureReason = "crypto unavailable" }; }
        public JsonObject EncryptRoomEvent(string roomId, string eventType, JsonObject content) { return null; }
        public Task EnsureRoomKeysSharedAsync(string roomId, IEnumerable<string> memberUserIds) { return Task.FromResult(0); }
        public void RotateOutbound(string roomId) { }
        public string RunSelfTest() { return "crypto unavailable on this platform"; }
        public void SignObject(JsonObject obj) { }
        public string ExportInboundSession(StoredInboundGroupSession s, out uint firstIndex) { firstIndex = 0; return null; }
        public bool ImportInboundSession(string roomId, string sessionKey, string senderKey, string ed25519, bool forwarded) { return false; }
#endif

        // ---- shared JSON helpers (all platforms) ----

        internal static JsonObject GetObj(JsonObject parent, string key)
        {
            try
            {
                if (parent != null && parent.ContainsKey(key) && parent[key].ValueType == JsonValueType.Object)
                    return parent.GetNamedObject(key);
            }
            catch { }
            return null;
        }

        internal static JsonObject Clone(JsonObject obj)
        {
            JsonObject c;
            return JsonObject.TryParse(obj.Stringify(), out c) ? c : new JsonObject();
        }
    }
}
