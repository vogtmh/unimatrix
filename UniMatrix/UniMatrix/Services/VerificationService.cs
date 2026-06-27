using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using UniMatrix.Crypto;
using UniMatrix.Data;

namespace UniMatrix.Services
{
    /// <summary>One emoji of a Short Authentication String (the glyph plus its English name).</summary>
    internal sealed class SasEmoji
    {
        public string Emoji;
        public string Name;
    }

    /// <summary>An incoming m.key.verification.request awaiting the user's accept/decline.</summary>
    internal sealed class VerificationRequest
    {
        public string TransactionId;
        public string UserId;
        public string DeviceId;
    }

    /// <summary>
    /// Drives interactive SAS (emoji) device verification over plaintext to-device events
    /// (m.key.verification.*). Supports being either the initiator (we call <see cref="StartAsync"/>)
    /// or the responder (a request arrives and the user accepts). On success the verified device's
    /// trust is raised to 2 (locally/SAS verified) and, when our cross-signing keys are unlocked, the
    /// device/master is additionally cross-signed.
    /// </summary>
    internal sealed class VerificationService
    {
        // Spec names for the m.sas.v1 method we implement.
        private const string MethodSas = "m.sas.v1";
        private const string KeyAgreement = "curve25519-hkdf-sha256";
        private const string HashSha256 = "sha256";
        private const string MacV2 = "hkdf-hmac-sha256.v2";
        private const string MacV1 = "hkdf-hmac-sha256";

        /// <summary>Raised when a remote device asks to verify; the UI should prompt accept/decline.</summary>
        public Action<VerificationRequest> OnIncomingRequest;

        /// <summary>Raised when the SAS is ready to be compared (txnId, 7 emoji, decimal fallback).</summary>
        public Action<string, IList<SasEmoji>, string> OnShowSas;

        /// <summary>Raised when a verification finishes (txnId, success, human-readable message).</summary>
        public Action<string, bool, string> OnComplete;

#if CRYPTO
        private readonly MatrixClient _client;
        private readonly CryptoService _crypto;
        private readonly MatrixDatabase _db;
        private readonly SemaphoreSlim _sem = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, SasTx> _txs = new Dictionary<string, SasTx>();

        public VerificationService(MatrixClient client, CryptoService crypto, MatrixDatabase db)
        {
            _client = client;
            _crypto = crypto;
            _db = db;
        }

        private sealed class SasTx
        {
            public string TransactionId;
            public string OtherUserId;
            public string OtherDeviceId;
            public bool WeStarted;          // are we the SAS initiator (we sent the start)?
            public OlmSas Sas;
            public string StartCanonical;   // canonical JSON of the start content (for the commitment)
            public string TheirCommitment;  // commitment we received in the accept (initiator side)
            public string MacMethod = MacV2;
            public string TheirKey;
            public JsonObject TheirMac;      // their reported per-key MACs
            public string TheirMacKeys;      // their MAC over the sorted key-id list
            public bool WeConfirmed;         // user pressed "they match" and we sent our MAC
            public long CreatedTs;
            public bool Done;
        }

        // ---- Public entry points ----

        /// <summary>Routes an incoming plaintext m.key.verification.* to-device event.</summary>
        public async Task HandleEventAsync(string type, string sender, JsonObject content)
        {
            if (content == null || string.IsNullOrEmpty(type)) return;
            string txn = MatrixClient.GetString(content, "transaction_id");
            if (string.IsNullOrEmpty(txn)) return;

            await _sem.WaitAsync();
            try
            {
                switch (type)
                {
                    case "m.key.verification.request": HandleRequest(txn, sender, content); break;
                    case "m.key.verification.ready": await HandleReadyAsync(txn, content); break;
                    case "m.key.verification.start": await HandleStartAsync(txn, sender, content); break;
                    case "m.key.verification.accept": await HandleAcceptAsync(txn, content); break;
                    case "m.key.verification.key": await HandleKeyAsync(txn, content); break;
                    case "m.key.verification.mac": await HandleMacAsync(txn, content); break;
                    case "m.key.verification.done": break; // peer confirmation; finalization already done
                    case "m.key.verification.cancel": HandleCancel(txn, content); break;
                }
            }
            catch (Exception ex) { App.Log("VERIFY: handle " + type + " failed: " + ex.Message); }
            finally { _sem.Release(); }
        }

        /// <summary>Begins verifying a specific device (we are the initiator). Returns the txn id.</summary>
        public async Task<string> StartAsync(string userId, string deviceId)
        {
            if (_crypto == null || !_crypto.Available) return null;
            string txn = MatrixClient.GenerateTxnId();
            await _sem.WaitAsync();
            try
            {
                var tx = new SasTx
                {
                    TransactionId = txn,
                    OtherUserId = userId,
                    OtherDeviceId = deviceId,
                    WeStarted = true,
                    CreatedTs = Now()
                };
                _txs[txn] = tx;

                var content = new JsonObject
                {
                    ["transaction_id"] = JsonValue.CreateStringValue(txn),
                    ["from_device"] = JsonValue.CreateStringValue(_crypto.DeviceId),
                    ["methods"] = new JsonArray { JsonValue.CreateStringValue(MethodSas) },
                    ["timestamp"] = JsonValue.CreateNumberValue(Now())
                };
                await SendAsync("m.key.verification.request", userId, deviceId, content);
            }
            catch (Exception ex) { App.Log("VERIFY: start failed: " + ex.Message); txn = null; }
            finally { _sem.Release(); }
            return txn;
        }

        /// <summary>Accepts an incoming request (sends ready; the requester then sends the start).</summary>
        public async Task AcceptIncomingAsync(string txnId)
        {
            await _sem.WaitAsync();
            try
            {
                SasTx tx; if (!_txs.TryGetValue(txnId, out tx)) return;
                var content = new JsonObject
                {
                    ["transaction_id"] = JsonValue.CreateStringValue(txnId),
                    ["from_device"] = JsonValue.CreateStringValue(_crypto.DeviceId),
                    ["methods"] = new JsonArray { JsonValue.CreateStringValue(MethodSas) }
                };
                await SendAsync("m.key.verification.ready", tx.OtherUserId, tx.OtherDeviceId, content);
            }
            catch (Exception ex) { App.Log("VERIFY: accept failed: " + ex.Message); }
            finally { _sem.Release(); }
        }

        /// <summary>The user confirmed the emoji match: send our MAC and finalize when theirs is in.</summary>
        public async Task ConfirmSasAsync(string txnId)
        {
            await _sem.WaitAsync();
            try
            {
                SasTx tx; if (!_txs.TryGetValue(txnId, out tx) || tx.Sas == null) return;
                await SendOurMacAsync(tx);
                tx.WeConfirmed = true;
                if (tx.TheirMac != null) await FinalizeAsync(tx);
            }
            catch (Exception ex) { App.Log("VERIFY: confirm failed: " + ex.Message); }
            finally { _sem.Release(); }
        }

        /// <summary>Cancels a verification with the given code/reason.</summary>
        public async Task CancelAsync(string txnId, string code, string reason)
        {
            await _sem.WaitAsync();
            try
            {
                SasTx tx; if (!_txs.TryGetValue(txnId, out tx)) return;
                await CancelTxAsync(tx, code, reason);
            }
            catch (Exception ex) { App.Log("VERIFY: cancel failed: " + ex.Message); }
            finally { _sem.Release(); }
        }

        // ---- Handlers (all called with _sem held) ----

        private void HandleRequest(string txn, string sender, JsonObject content)
        {
            string fromDevice = MatrixClient.GetString(content, "from_device");
            if (string.IsNullOrEmpty(fromDevice)) return;
            if (!StrList(content, "methods").Contains(MethodSas)) return; // we only do SAS

            var tx = new SasTx
            {
                TransactionId = txn,
                OtherUserId = sender,
                OtherDeviceId = fromDevice,
                WeStarted = false,
                CreatedTs = Now()
            };
            _txs[txn] = tx;
            if (OnIncomingRequest != null)
                OnIncomingRequest(new VerificationRequest { TransactionId = txn, UserId = sender, DeviceId = fromDevice });
        }

        private async Task HandleReadyAsync(string txn, JsonObject content)
        {
            SasTx tx; if (!_txs.TryGetValue(txn, out tx) || !tx.WeStarted) return;
            string fromDevice = MatrixClient.GetString(content, "from_device");
            if (!string.IsNullOrEmpty(fromDevice)) tx.OtherDeviceId = fromDevice;
            await SendStartAsync(tx);
        }

        private async Task SendStartAsync(SasTx tx)
        {
            var content = new JsonObject
            {
                ["transaction_id"] = JsonValue.CreateStringValue(tx.TransactionId),
                ["from_device"] = JsonValue.CreateStringValue(_crypto.DeviceId),
                ["method"] = JsonValue.CreateStringValue(MethodSas),
                ["key_agreement_protocols"] = new JsonArray { JsonValue.CreateStringValue(KeyAgreement) },
                ["hashes"] = new JsonArray { JsonValue.CreateStringValue(HashSha256) },
                ["message_authentication_codes"] = new JsonArray
                {
                    JsonValue.CreateStringValue(MacV2), JsonValue.CreateStringValue(MacV1)
                },
                ["short_authentication_string"] = new JsonArray
                {
                    JsonValue.CreateStringValue("emoji"), JsonValue.CreateStringValue("decimal")
                }
            };
            tx.StartCanonical = CanonicalJson.Serialize(content);
            tx.WeStarted = true;
            await SendAsync("m.key.verification.start", tx.OtherUserId, tx.OtherDeviceId, content);
        }

        private async Task HandleStartAsync(string txn, string sender, JsonObject content)
        {
            if (MatrixClient.GetString(content, "method") != MethodSas)
            {
                await SendCancelAsync(sender, MatrixClient.GetString(content, "from_device"), txn,
                    "m.unknown_method", "only m.sas.v1 supported");
                return;
            }

            SasTx tx; _txs.TryGetValue(txn, out tx);
            // Glare: if we also started, the device with the lexicographically smaller (user,device)
            // is the initiator; the other yields and becomes the responder.
            if (tx != null && tx.WeStarted)
            {
                string ours = _client.UserId + "|" + _crypto.DeviceId;
                string theirs = sender + "|" + MatrixClient.GetString(content, "from_device");
                if (string.CompareOrdinal(ours, theirs) < 0) return; // we win, ignore their start
            }

            if (!StrList(content, "key_agreement_protocols").Contains(KeyAgreement))
            {
                await SendCancelAsync(sender, MatrixClient.GetString(content, "from_device"), txn,
                    "m.unknown_method", "no shared key agreement");
                return;
            }
            var macList = StrList(content, "message_authentication_codes");
            string macMethod = macList.Contains(MacV2) ? MacV2 : (macList.Contains(MacV1) ? MacV1 : null);
            if (macMethod == null)
            {
                await SendCancelAsync(sender, MatrixClient.GetString(content, "from_device"), txn,
                    "m.unknown_method", "no shared MAC");
                return;
            }
            var sasList = StrList(content, "short_authentication_string");
            var chosenSas = new JsonArray();
            if (sasList.Contains("emoji")) chosenSas.Add(JsonValue.CreateStringValue("emoji"));
            if (sasList.Contains("decimal")) chosenSas.Add(JsonValue.CreateStringValue("decimal"));
            if (chosenSas.Count == 0)
            {
                await SendCancelAsync(sender, MatrixClient.GetString(content, "from_device"), txn,
                    "m.unknown_method", "no shared SAS");
                return;
            }

            string fromDevice = MatrixClient.GetString(content, "from_device");
            if (tx == null)
            {
                tx = new SasTx { TransactionId = txn, OtherUserId = sender, CreatedTs = Now() };
                _txs[txn] = tx;
            }
            tx.OtherDeviceId = fromDevice ?? tx.OtherDeviceId;
            tx.WeStarted = false;
            tx.MacMethod = macMethod;
            tx.StartCanonical = CanonicalJson.Serialize(content);
            tx.Sas = OlmSas.Create();

            string ourPub = tx.Sas.PublicKey();
            string commitment;
            using (var u = new OlmUtility()) commitment = u.Sha256(ourPub + tx.StartCanonical);

            var accept = new JsonObject
            {
                ["transaction_id"] = JsonValue.CreateStringValue(txn),
                ["key_agreement_protocol"] = JsonValue.CreateStringValue(KeyAgreement),
                ["hash"] = JsonValue.CreateStringValue(HashSha256),
                ["message_authentication_code"] = JsonValue.CreateStringValue(macMethod),
                ["short_authentication_string"] = chosenSas,
                ["commitment"] = JsonValue.CreateStringValue(commitment)
            };
            await SendAsync("m.key.verification.accept", tx.OtherUserId, tx.OtherDeviceId, accept);
        }

        private async Task HandleAcceptAsync(string txn, JsonObject content)
        {
            SasTx tx; if (!_txs.TryGetValue(txn, out tx) || !tx.WeStarted) return;
            tx.TheirCommitment = MatrixClient.GetString(content, "commitment");
            string macMethod = MatrixClient.GetString(content, "message_authentication_code");
            if (macMethod == MacV2 || macMethod == MacV1) tx.MacMethod = macMethod;
            tx.Sas = OlmSas.Create();
            var keyMsg = new JsonObject
            {
                ["transaction_id"] = JsonValue.CreateStringValue(txn),
                ["key"] = JsonValue.CreateStringValue(tx.Sas.PublicKey())
            };
            await SendAsync("m.key.verification.key", tx.OtherUserId, tx.OtherDeviceId, keyMsg);
        }

        private async Task HandleKeyAsync(string txn, JsonObject content)
        {
            SasTx tx; if (!_txs.TryGetValue(txn, out tx) || tx.Sas == null) return;
            string theirKey = MatrixClient.GetString(content, "key");
            if (string.IsNullOrEmpty(theirKey)) return;

            if (tx.WeStarted)
            {
                // Initiator: verify the responder's commitment before trusting their key.
                string expect;
                using (var u = new OlmUtility()) expect = u.Sha256(theirKey + tx.StartCanonical);
                if (expect != tx.TheirCommitment)
                {
                    await CancelTxAsync(tx, "m.mismatched_commitment", "commitment mismatch");
                    return;
                }
                tx.TheirKey = theirKey;
                tx.Sas.SetTheirKey(theirKey);
                ComputeAndShowSas(tx);
            }
            else
            {
                // Responder: accept their key, reply with ours, then show the SAS.
                tx.TheirKey = theirKey;
                tx.Sas.SetTheirKey(theirKey);
                var keyMsg = new JsonObject
                {
                    ["transaction_id"] = JsonValue.CreateStringValue(txn),
                    ["key"] = JsonValue.CreateStringValue(tx.Sas.PublicKey())
                };
                await SendAsync("m.key.verification.key", tx.OtherUserId, tx.OtherDeviceId, keyMsg);
                ComputeAndShowSas(tx);
            }
        }

        private async Task HandleMacAsync(string txn, JsonObject content)
        {
            SasTx tx; if (!_txs.TryGetValue(txn, out tx)) return;
            tx.TheirMac = CryptoService.GetObj(content, "mac");
            tx.TheirMacKeys = MatrixClient.GetString(content, "keys");
            if (tx.WeConfirmed) await FinalizeAsync(tx);
        }

        private void HandleCancel(string txn, JsonObject content)
        {
            SasTx tx; if (!_txs.TryGetValue(txn, out tx)) return;
            string code = MatrixClient.GetString(content, "code");
            Complete(txn, false, "Cancelled by other device" + (string.IsNullOrEmpty(code) ? "" : " (" + code + ")"));
            Cleanup(tx);
        }

        // ---- SAS computation + MACs ----

        private void ComputeAndShowSas(SasTx tx)
        {
            string ourKey = tx.Sas.PublicKey();
            string theirKey = tx.TheirKey;
            string ourUser = _client.UserId, ourDevice = _crypto.DeviceId;
            string iU, iD, iK, rU, rD, rK;
            if (tx.WeStarted)
            {
                iU = ourUser; iD = ourDevice; iK = ourKey;
                rU = tx.OtherUserId; rD = tx.OtherDeviceId; rK = theirKey;
            }
            else
            {
                iU = tx.OtherUserId; iD = tx.OtherDeviceId; iK = theirKey;
                rU = ourUser; rD = ourDevice; rK = ourKey;
            }
            string info = "MATRIX_KEY_VERIFICATION_SAS|" + iU + "|" + iD + "|" + iK + "|" +
                          rU + "|" + rD + "|" + rK + "|" + tx.TransactionId;
            byte[] bytes = tx.Sas.GenerateBytes(info, 6);
            var emojis = EmojiFromBytes(bytes);
            string dec = DecimalFromBytes(bytes);
            if (OnShowSas != null) OnShowSas(tx.TransactionId, emojis, dec);
        }

        private async Task SendOurMacAsync(SasTx tx)
        {
            string ourUser = _client.UserId, ourDevice = _crypto.DeviceId;
            string baseInfo = "MATRIX_KEY_VERIFICATION_MAC" + ourUser + ourDevice +
                              tx.OtherUserId + tx.OtherDeviceId + tx.TransactionId;

            var keysToMac = new Dictionary<string, string>();
            keysToMac["ed25519:" + ourDevice] = _crypto.IdentityEd25519;
            if (!string.IsNullOrEmpty(_crypto.MasterPublicKey))
                keysToMac["ed25519:" + _crypto.MasterPublicKey] = _crypto.MasterPublicKey;

            var ids = new List<string>(keysToMac.Keys);
            ids.Sort(StringComparer.Ordinal);

            var macMap = new JsonObject();
            foreach (var keyId in ids)
                macMap[keyId] = JsonValue.CreateStringValue(Mac(tx, keysToMac[keyId], baseInfo + keyId));
            string keysMac = Mac(tx, string.Join(",", ids), baseInfo + "KEY_IDS");

            var content = new JsonObject
            {
                ["transaction_id"] = JsonValue.CreateStringValue(tx.TransactionId),
                ["mac"] = macMap,
                ["keys"] = JsonValue.CreateStringValue(keysMac)
            };
            await SendAsync("m.key.verification.mac", tx.OtherUserId, tx.OtherDeviceId, content);
        }

        private async Task FinalizeAsync(SasTx tx)
        {
            if (tx.Done) return;
            if (tx.TheirMac == null) { await CancelTxAsync(tx, "m.invalid_message", "no MAC received"); return; }

            string baseInfo = "MATRIX_KEY_VERIFICATION_MAC" + tx.OtherUserId + tx.OtherDeviceId +
                              _client.UserId + _crypto.DeviceId + tx.TransactionId;

            var keyIds = new List<string>(tx.TheirMac.Keys);
            keyIds.Sort(StringComparer.Ordinal);
            string expectKeys = Mac(tx, string.Join(",", keyIds), baseInfo + "KEY_IDS");
            if (expectKeys != tx.TheirMacKeys)
            {
                await CancelTxAsync(tx, "m.key_mismatch", "key-list MAC mismatch");
                return;
            }

            var csTheir = _db.GetCrossSigningKeys(tx.OtherUserId);
            bool deviceVerified = false;
            string verifiedMaster = null;

            foreach (var keyId in keyIds)
            {
                if (!keyId.StartsWith("ed25519:", StringComparison.Ordinal)) continue;
                string id = keyId.Substring("ed25519:".Length);
                string providedMac = MatrixClient.GetString(tx.TheirMac, keyId);
                string keyValue = null;
                bool isDevice = false;
                if (id == tx.OtherDeviceId)
                {
                    var dev = _db.GetDevice(tx.OtherUserId, tx.OtherDeviceId);
                    keyValue = dev != null ? dev.Ed25519 : null;
                    isDevice = true;
                }
                else if (csTheir != null && id == csTheir.MasterKey)
                {
                    keyValue = csTheir.MasterKey;
                }
                if (string.IsNullOrEmpty(keyValue)) continue;

                string expect = Mac(tx, keyValue, baseInfo + keyId);
                if (expect != providedMac)
                {
                    await CancelTxAsync(tx, "m.key_mismatch", "MAC mismatch for " + keyId);
                    return;
                }
                if (isDevice) deviceVerified = true;
                else verifiedMaster = keyValue;
            }

            if (!deviceVerified)
            {
                await CancelTxAsync(tx, "m.key_mismatch", "device key was not authenticated");
                return;
            }

            // Persist the device as locally/SAS verified (trust = 2).
            var stored = _db.GetDevice(tx.OtherUserId, tx.OtherDeviceId);
            if (stored != null) { stored.Trust = 2; _db.SaveDevice(stored); }

            await SendAsync("m.key.verification.done", tx.OtherUserId, tx.OtherDeviceId,
                new JsonObject { ["transaction_id"] = JsonValue.CreateStringValue(tx.TransactionId) });

            // Best-effort cross-signing now that we trust them.
            try
            {
                if (tx.OtherUserId == _client.UserId)
                    await _crypto.CrossSignOwnDeviceAsync(tx.OtherDeviceId);
                else if (!string.IsNullOrEmpty(verifiedMaster))
                    await _crypto.CrossSignUserMasterAsync(tx.OtherUserId, verifiedMaster);
            }
            catch (Exception ex) { App.Log("VERIFY: post-verify cross-sign failed: " + ex.Message); }

            tx.Done = true;
            App.Log("VERIFY: " + tx.OtherUserId + "/" + tx.OtherDeviceId + " verified via SAS");
            Complete(tx.TransactionId, true, "Verified");
            Cleanup(tx);
        }

        private string Mac(SasTx tx, string input, string info)
        {
            return tx.MacMethod == MacV2 ? tx.Sas.CalculateMacFixed(input, info) : tx.Sas.CalculateMac(input, info);
        }

        // ---- Cancel / cleanup ----

        private async Task CancelTxAsync(SasTx tx, string code, string reason)
        {
            try
            {
                await SendCancelAsync(tx.OtherUserId, tx.OtherDeviceId, tx.TransactionId, code, reason);
            }
            catch { }
            Complete(tx.TransactionId, false, reason);
            Cleanup(tx);
        }

        private async Task SendCancelAsync(string user, string device, string txn, string code, string reason)
        {
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(device)) return;
            var content = new JsonObject
            {
                ["transaction_id"] = JsonValue.CreateStringValue(txn),
                ["code"] = JsonValue.CreateStringValue(code),
                ["reason"] = JsonValue.CreateStringValue(reason ?? code)
            };
            await SendAsync("m.key.verification.cancel", user, device, content);
        }

        private void Complete(string txn, bool success, string message)
        {
            if (OnComplete != null) OnComplete(txn, success, message);
        }

        private void Cleanup(SasTx tx)
        {
            try { if (tx.Sas != null) { tx.Sas.Dispose(); tx.Sas = null; } } catch { }
            _txs.Remove(tx.TransactionId);
        }

        private async Task SendAsync(string type, string toUser, string toDevice, JsonObject content)
        {
            var messages = new JsonObject { [toUser] = new JsonObject { [toDevice] = content } };
            await _client.SendToDeviceAsync(type, messages);
        }

        // ---- SAS byte decoding ----

        private static IList<SasEmoji> EmojiFromBytes(byte[] b)
        {
            var idx = new int[7];
            idx[0] = b[0] >> 2;
            idx[1] = ((b[0] & 0x3) << 4) | (b[1] >> 4);
            idx[2] = ((b[1] & 0xf) << 2) | (b[2] >> 6);
            idx[3] = b[2] & 0x3f;
            idx[4] = b[3] >> 2;
            idx[5] = ((b[3] & 0x3) << 4) | (b[4] >> 4);
            idx[6] = ((b[4] & 0xf) << 2) | (b[5] >> 6);

            var list = new List<SasEmoji>(7);
            foreach (var i in idx)
                list.Add(new SasEmoji { Emoji = EmojiTable[i, 0], Name = EmojiTable[i, 1] });
            return list;
        }

        private static string DecimalFromBytes(byte[] b)
        {
            int d1 = ((b[0] << 5) | (b[1] >> 3)) & 0x1fff;
            int d2 = (((b[1] & 0x7) << 10) | (b[2] << 2) | (b[3] >> 6)) & 0x1fff;
            int d3 = (((b[3] & 0x3f) << 7) | (b[4] >> 1)) & 0x1fff;
            return (d1 + 1000) + " " + (d2 + 1000) + " " + (d3 + 1000);
        }

        private static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

        private static List<string> StrList(JsonObject o, string key)
        {
            var r = new List<string>();
            try
            {
                if (o != null && o.ContainsKey(key) && o[key].ValueType == JsonValueType.Array)
                    foreach (var v in o.GetNamedArray(key))
                        if (v.ValueType == JsonValueType.String) r.Add(v.GetString());
            }
            catch { }
            return r;
        }

        // The 64-entry Matrix SAS emoji table (index order is normative).
        private static readonly string[,] EmojiTable = new string[64, 2]
        {
            { "\U0001F436", "Dog" }, { "\U0001F431", "Cat" }, { "\U0001F981", "Lion" },
            { "\U0001F40E", "Horse" }, { "\U0001F984", "Unicorn" }, { "\U0001F437", "Pig" },
            { "\U0001F418", "Elephant" }, { "\U0001F430", "Rabbit" }, { "\U0001F43C", "Panda" },
            { "\U0001F413", "Rooster" }, { "\U0001F427", "Penguin" }, { "\U0001F422", "Turtle" },
            { "\U0001F41F", "Fish" }, { "\U0001F419", "Octopus" }, { "\U0001F98B", "Butterfly" },
            { "\U0001F337", "Flower" }, { "\U0001F333", "Tree" }, { "\U0001F335", "Cactus" },
            { "\U0001F344", "Mushroom" }, { "\U0001F30F", "Globe" }, { "\U0001F319", "Moon" },
            { "\u2601\uFE0F", "Cloud" }, { "\U0001F525", "Fire" }, { "\U0001F34C", "Banana" },
            { "\U0001F34E", "Apple" }, { "\U0001F353", "Strawberry" }, { "\U0001F33D", "Corn" },
            { "\U0001F355", "Pizza" }, { "\U0001F382", "Cake" }, { "\u2764\uFE0F", "Heart" },
            { "\U0001F600", "Smiley" }, { "\U0001F916", "Robot" }, { "\U0001F3A9", "Hat" },
            { "\U0001F453", "Glasses" }, { "\U0001F527", "Spanner" }, { "\U0001F385", "Santa" },
            { "\U0001F44D", "Thumbs Up" }, { "\u2602\uFE0F", "Umbrella" }, { "\u231B", "Hourglass" },
            { "\u23F0", "Clock" }, { "\U0001F381", "Gift" }, { "\U0001F4A1", "Light Bulb" },
            { "\U0001F4D5", "Book" }, { "\u270F\uFE0F", "Pencil" }, { "\U0001F4CE", "Paperclip" },
            { "\u2702\uFE0F", "Scissors" }, { "\U0001F512", "Lock" }, { "\U0001F511", "Key" },
            { "\U0001F528", "Hammer" }, { "\u260E\uFE0F", "Telephone" }, { "\U0001F3C1", "Flag" },
            { "\U0001F682", "Train" }, { "\U0001F6B2", "Bicycle" }, { "\u2708\uFE0F", "Aeroplane" },
            { "\U0001F680", "Rocket" }, { "\U0001F3C6", "Trophy" }, { "\u26BD", "Ball" },
            { "\U0001F3B8", "Guitar" }, { "\U0001F3BA", "Trumpet" }, { "\U0001F514", "Bell" },
            { "\u2693", "Anchor" }, { "\U0001F3A7", "Headphones" }, { "\U0001F4C1", "Folder" },
            { "\U0001F4CC", "Pin" }
        };
#else
        public VerificationService(MatrixClient client, CryptoService crypto, MatrixDatabase db) { }
        public Task HandleEventAsync(string type, string sender, JsonObject content) { return Task.FromResult(0); }
        public Task<string> StartAsync(string userId, string deviceId) { return Task.FromResult<string>(null); }
        public Task AcceptIncomingAsync(string txnId) { return Task.FromResult(0); }
        public Task ConfirmSasAsync(string txnId) { return Task.FromResult(0); }
        public Task CancelAsync(string txnId, string code, string reason) { return Task.FromResult(0); }
#endif
    }
}
