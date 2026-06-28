using System;
using System.Collections.Generic;
using Windows.Data.Json;
using UniMatrix.Data;
using UniMatrix.Models;

namespace UniMatrix.Services
{
    /// <summary>Summary of what a /sync pass changed, so the UI can refresh selectively.</summary>
    internal class SyncResult
    {
        public string NextBatch { get; set; }
        public HashSet<string> ChangedRooms { get; } = new HashSet<string>();
        public bool HasChanges { get { return ChangedRooms.Count > 0; } }

        /// <summary>
        /// Raw WebRTC call signalling events (m.call.*) seen in this sync pass, in arrival order.
        /// The sync loop hands these to the CallService on the UI thread. We pass the raw content
        /// through rather than the timeline marker because the CallService needs the full SDP/ICE
        /// payload, not just a display label.
        /// </summary>
        public List<CallSignal> CallSignals { get; } = new List<CallSignal>();

        // ---- End-to-end encryption (consumed by the sync loop, which owns the CryptoService) ----

        /// <summary>Server's count of our published signed_curve25519 one-time keys, when reported.</summary>
        public int? OneTimeKeyCount { get; set; }

        /// <summary>Users whose device list changed (need a /keys/query refresh).</summary>
        public List<string> DeviceListChanged { get; } = new List<string>();

        /// <summary>Users we no longer share an encrypted room with (drop their devices).</summary>
        public List<string> DeviceListLeft { get; } = new List<string>();

        /// <summary>Raw to-device events (m.room.encrypted Olm messages carrying room keys, secrets).</summary>
        public List<JsonObject> ToDeviceEvents { get; } = new List<JsonObject>();

        /// <summary>Encrypted room timeline events awaiting Megolm decryption by the sync loop.</summary>
        public List<EncryptedTimelineEvent> EncryptedEvents { get; } = new List<EncryptedTimelineEvent>();

        // ---- MatrixRTC (Element Call / Matrix 2.0) awareness ----

        /// <summary>
        /// MSC4075 ring notifications (m.rtc.notification) seen this sync. UniMatrix can't join the
        /// LiveKit SFU media, so these only drive the incoming-call ring + a timeline tile; the call
        /// itself is answered in Element. The sync loop dispatches them on the UI thread.
        /// </summary>
        public List<MatrixRtcNotification> MatrixRtcNotifications { get; } = new List<MatrixRtcNotification>();

        /// <summary>
        /// MSC4143 call-membership state changes (m.rtc.member) seen this sync. Used to tell when a
        /// MatrixRTC call in a room has started or ended (so a stale ring can be dismissed).
        /// </summary>
        public List<MatrixRtcMembership> MatrixRtcMemberships { get; } = new List<MatrixRtcMembership>();
    }

    /// <summary>A stored m.room.encrypted timeline event the sync loop will try to decrypt.</summary>
    internal class EncryptedTimelineEvent
    {
        public string RoomId { get; set; }
        public string EventId { get; set; }
        public string Sender { get; set; }
        public long Timestamp { get; set; }
        public JsonObject Content { get; set; }
    }


    /// <summary>A single m.call.* signalling event captured from /sync for the CallService.</summary>
    internal class CallSignal
    {
        public string RoomId { get; set; }
        public string Type { get; set; }      // e.g. "m.call.invite"
        public string Sender { get; set; }
        public long Timestamp { get; set; }    // origin_server_ts
        public JsonObject Content { get; set; }
    }

    /// <summary>A MatrixRTC ring notification (MSC4075 m.rtc.notification) captured from /sync.</summary>
    internal class MatrixRtcNotification
    {
        public string RoomId { get; set; }
        public string EventId { get; set; }
        public string Sender { get; set; }
        public long Timestamp { get; set; }            // origin_server_ts
        public string NotificationType { get; set; }   // "ring" or "notification"
        public long Lifetime { get; set; }             // ms the ring stays valid; 0 = unspecified
        public string ParentId { get; set; }           // m.relates_to.event_id (call identity), if any
    }

    /// <summary>A MatrixRTC call-membership change (MSC4143 m.rtc.member state event).</summary>
    internal class MatrixRtcMembership
    {
        public string RoomId { get; set; }
        public string EventId { get; set; }    // the state event's id (per-publish; used to dedupe rings)
        public string StateKey { get; set; }   // the membership's state key (user/device scoped)
        public string UserId { get; set; }     // best-effort user id parsed from the state key
        public string Sender { get; set; }     // the event sender (clean user id, for self-checks)
        public long Timestamp { get; set; }    // origin_server_ts (to drop stale joins on backfill)
        public bool Active { get; set; }       // true = in the call, false = left

        // LiveKit focus info (from the join's foci_preferred[0]/focus_active), used to obtain an SFU
        // token and connect. Null on a leave (empty content) or non-LiveKit focus.
        public string FocusServiceUrl { get; set; }  // e.g. https://livekit-jwt.call.matrix.org
        public string FocusRoomAlias { get; set; }   // LiveKit room name (livekit_alias), usually the room id
        public string CallId { get; set; }           // call_id from content (usually "" for the room's call)
    }

    /// <summary>
    /// Parses a Matrix /sync response and persists rooms, timeline messages and
    /// member state into the local database.
    /// </summary>
    internal class SyncProcessor
    {
        private readonly MatrixDatabase _db;
        private readonly string _myUserId;

        public SyncProcessor(MatrixDatabase db, string myUserId)
        {
            _db = db;
            _myUserId = myUserId;
        }

        public SyncResult Process(JsonObject sync)
        {
            var result = new SyncResult();
            if (sync == null) return result;

            result.NextBatch = MatrixClient.GetString(sync, "next_batch");

            // Account data carries the m.direct map (which rooms are 1:1 DMs). It only appears in a
            // sync when it changes, so process it whenever present and flag the listed rooms.
            ProcessAccountData(GetObject(sync, "account_data"));

            // ---- End-to-end encryption: device tracking, OTK upkeep, to-device key delivery ----
            ProcessE2ee(sync, result);

            JsonObject rooms = GetObject(sync, "rooms");
            if (rooms == null) return result;

            JsonObject join = GetObject(rooms, "join");
            JsonObject invite = GetObject(rooms, "invite");
            JsonObject leave = GetObject(rooms, "leave");
            App.Log("SYNC rooms: join=" + (join != null ? join.Keys.Count : 0) +
                    " invite=" + (invite != null ? invite.Keys.Count : 0) +
                    " leave=" + (leave != null ? leave.Keys.Count : 0));

            if (join != null)
            {
                foreach (var roomId in join.Keys)
                {
                    try
                    {
                        JsonObject roomObj = GetObject(join, roomId);
                        if (roomObj == null) continue;
                        ProcessJoinedRoom(roomId, roomObj, result);
                    }
                    catch (Exception ex)
                    {
                        // Don't let one malformed room abort the entire sync.
                        App.Log("SYNC room parse error (" + roomId + "): " + ex.Message);
                    }
                }
            }

            // Rooms the user has been invited to but not joined. Without this, an invitation
            // (including a new direct message) would never surface in the app.
            if (invite != null)
            {
                foreach (var roomId in invite.Keys)
                {
                    try
                    {
                        App.Log("SYNC invite received: " + roomId);
                        JsonObject roomObj = GetObject(invite, roomId);
                        if (roomObj == null) continue;
                        ProcessInvitedRoom(roomId, roomObj, result);
                    }
                    catch (Exception ex)
                    {
                        App.Log("SYNC invite parse error (" + roomId + "): " + ex.Message);
                    }
                }
            }

            // Rooms the user has left / declined / been removed from: drop them locally so a
            // declined invite or a left room disappears from the list on the next sync.
            if (leave != null)
            {
                foreach (var roomId in leave.Keys)
                {
                    try
                    {
                        _db.DeleteRoom(roomId);
                        result.ChangedRooms.Add(roomId);
                    }
                    catch (Exception ex)
                    {
                        App.Log("SYNC leave parse error (" + roomId + "): " + ex.Message);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the encryption-related parts of a /sync response: the published one-time-key
        /// count (so we can top up), device-list change/left notices (so we re-query or drop device
        /// keys), and to-device events (Olm messages delivering room keys and secrets). The actual
        /// cryptography happens in the sync loop, which owns the CryptoService; here we only collect.
        /// </summary>
        private void ProcessE2ee(JsonObject sync, SyncResult result)
        {
            try
            {
                JsonObject otkCounts = GetObject(sync, "device_one_time_keys_count");
                if (otkCounts != null && otkCounts.ContainsKey("signed_curve25519"))
                    result.OneTimeKeyCount = (int)GetNumber(otkCounts, "signed_curve25519", 0);

                JsonObject deviceLists = GetObject(sync, "device_lists");
                if (deviceLists != null)
                {
                    JsonArray changed = GetArray(deviceLists, "changed");
                    if (changed != null)
                        foreach (var v in changed) if (v.ValueType == JsonValueType.String) result.DeviceListChanged.Add(v.GetString());

                    JsonArray left = GetArray(deviceLists, "left");
                    if (left != null)
                        foreach (var v in left) if (v.ValueType == JsonValueType.String) result.DeviceListLeft.Add(v.GetString());
                }

                JsonObject toDevice = GetObject(sync, "to_device");
                JsonArray events = GetArray(toDevice, "events");
                if (events != null)
                    foreach (var v in events) if (v.ValueType == JsonValueType.Object)
                    {
                        LogRawEvent("RX-todevice", null, v.GetObject());
                        result.ToDeviceEvents.Add(v.GetObject());
                    }
            }
            catch (Exception ex)
            {
                App.Log("SYNC e2ee parse error: " + ex.Message);
            }
        }

        /// <summary>
        /// Parses the top-level account_data for the m.direct event, which maps each user id to the
        /// list of room ids the user treats as a direct (1:1) chat. Every listed room is flagged as
        /// direct in the database so the UI and the notification toggles can distinguish DMs from
        /// group rooms. Rooms not listed are left as-is (un-marking a DM is rare and not signalled
        /// reliably, so we don't clear the flag here).
        /// </summary>
        private void ProcessAccountData(JsonObject accountData)
        {
            JsonArray events = GetArray(accountData, "events");
            if (events == null) return;

            foreach (var evVal in events)
            {
                try
                {
                    JsonObject ev = evVal.GetObject();
                    if (MatrixClient.GetString(ev, "type") != "m.direct") continue;

                    JsonObject content = GetObject(ev, "content");
                    if (content == null) continue;

                    // content is { "@user:server": ["!room1", "!room2"], ... }
                    foreach (var userId in content.Keys)
                    {
                        if (content[userId].ValueType != JsonValueType.Array) continue;
                        foreach (var roomVal in content.GetNamedArray(userId))
                        {
                            if (roomVal.ValueType != JsonValueType.String) continue;
                            string roomId = roomVal.GetString();
                            if (!string.IsNullOrEmpty(roomId))
                                _db.SetRoomDirect(roomId, true);
                        }
                    }
                }
                catch (Exception ex)
                {
                    App.Log("SYNC m.direct parse error: " + ex.Message);
                }
            }
        }

        private void ProcessJoinedRoom(string roomId, JsonObject roomObj, SyncResult result)
        {
            var room = new Room { Id = roomId };

            // ---- State events (room name, avatar, topic, membership) ----
            JsonObject state = GetObject(roomObj, "state");
            if (state != null)
            {
                JsonArray events = GetArray(state, "events");
                if (events != null)
                {
                    foreach (var ev in events)
                    {
                        LogRawEvent("RX-state", roomId, ev.GetObject());
                        ApplyStateEvent(roomId, ev.GetObject(), room, result, "state");
                    }
                }
            }

            // ---- Timeline (messages + inline state) ----
            long latestTs = 0;
            string preview = null;
            JsonObject timeline = GetObject(roomObj, "timeline");
            if (timeline != null)
            {
                // Persist the room's prev_batch token: this is the canonical anchor for paging
                // history backward via /messages?dir=b. Backfill uses it as the starting point so
                // it resumes from a server-valid position instead of an empty token (which made
                // pagination unreliable and could dead-end after only the most recent messages).
                // We refresh it every sync so it always points just before the newest timeline,
                // which is also the best place to re-anchor recovery from if a room was wrongly
                // marked "fully fetched".
                string prevBatch = MatrixClient.GetString(timeline, "prev_batch");
                if (!string.IsNullOrEmpty(prevBatch))
                    _db.SetMeta("pb_" + roomId, prevBatch);

                JsonArray events = GetArray(timeline, "events");

                // DIAGNOSTIC (incoming-message loss): a "limited" timeline means the server is
                // signalling a GAP — more events accumulated since our last /sync than the filter's
                // timeline limit (20), so only the most recent ones are in this window and the rest
                // were NOT delivered. We do not currently backfill that gap, so those messages are
                // silently lost. This is the likely cause of intermittently-missed incoming messages
                // (e.g. after the phone resumes from suspend and the sender's backlog overflows 20).
                bool limited = false;
                try
                {
                    if (timeline.ContainsKey("limited") && timeline["limited"].ValueType == JsonValueType.Boolean)
                        limited = timeline.GetNamedBoolean("limited");
                }
                catch { }
                int evCount = events != null ? events.Count : 0;
                if (limited || evCount > 0)
                    App.Log("SYNC timeline room=" + roomId + " limited=" + limited + " events=" + evCount +
                            " prevBatch=" + (string.IsNullOrEmpty(prevBatch) ? "<none>" : "set") +
                            (limited ? "  *** GAP — earlier messages in this room were NOT fetched (possible message loss) ***" : ""));

                if (events != null)
                {
                    foreach (var evVal in events)
                    {
                        JsonObject ev = evVal.GetObject();
                        string type = MatrixClient.GetString(ev, "type");
                        LogRawEvent("RX", roomId, ev);

                        if (type == "m.room.message")
                        {
                            var msg = ApplyMessageEvent(roomId, ev);
                            if (msg != null)
                            {
                                if (msg.Timestamp > latestTs)
                                {
                                    latestTs = msg.Timestamp;
                                    preview = BuildPreview(msg);
                                }
                            }
                        }
                        else if (type == "m.room.encrypted")
                        {
                            // Store the ciphertext row and surface it for the sync loop to decrypt
                            // with the CryptoService. We keep the body as the raw content JSON so a
                            // decrypt can be retried later (e.g. once the room key arrives).
                            var enc = ApplyEncryptedEvent(roomId, ev, result);
                            if (enc != null && enc.Timestamp > latestTs)
                            {
                                latestTs = enc.Timestamp;
                                preview = "\uD83D\uDD12 Encrypted message";
                            }
                        }
                        else if (type != null && type.StartsWith("m.call."))
                        {
                            // Capture the raw signalling event for the CallService (it needs the full
                            // SDP/ICE payload). The sync loop delivers these on the UI thread.
                            JsonObject callContent = GetObject(ev, "content");
                            if (callContent != null)
                            {
                                result.CallSignals.Add(new CallSignal
                                {
                                    RoomId = roomId,
                                    Type = type,
                                    Sender = MatrixClient.GetString(ev, "sender"),
                                    Timestamp = (long)GetNumber(ev, "origin_server_ts", 0),
                                    Content = callContent
                                });
                            }

                            var msg = ApplyCallEvent(roomId, type, ev);
                            if (msg != null && msg.Timestamp > latestTs)
                            {
                                latestTs = msg.Timestamp;
                                preview = BuildPreview(msg);
                            }
                        }
                        else if (IsRtcNotificationType(type))
                        {
                            // MatrixRTC (Element Call) ring in a plaintext room. Surface it for the
                            // incoming-call ring and drop a timeline tile (encrypted rooms take the
                            // decrypted path in MainPage.Crypto, which reuses the same helpers).
                            JsonObject rtcContent = GetObject(ev, "content");
                            string rtcEventId = MatrixClient.GetString(ev, "event_id");
                            string rtcSender = MatrixClient.GetString(ev, "sender");
                            long rtcTs = (long)GetNumber(ev, "origin_server_ts", 0);
                            var n = ParseRtcNotification(roomId, rtcEventId, rtcSender, rtcTs, rtcContent);
                            if (n != null)
                            {
                                result.MatrixRtcNotifications.Add(n);
                                var tile = BuildRtcMissedTile(roomId, n.ParentId ?? rtcEventId, rtcSender, rtcTs, _myUserId);
                                if (tile != null)
                                {
                                    _db.UpsertMessage(tile);
                                    if (tile.Timestamp > latestTs) { latestTs = tile.Timestamp; preview = BuildPreview(tile); }
                                }
                            }
                        }
                        else if (IsRtcMemberType(type))
                        {
                            // MatrixRTC call-membership change carried inline in the timeline.
                            ApplyStateEvent(roomId, ev, room, result, "timeline");
                        }
                        else if (type == "m.room.name" || type == "m.room.avatar" ||
                                 type == "m.room.topic" || type == "m.room.member" ||
                                 type == "m.room.encryption")
                        {
                            ApplyStateEvent(roomId, ev, room);
                        }
                    }
                }
            }

            // ---- Unread + member count ----
            JsonObject unreadObj = GetObject(roomObj, "unread_notifications");
            int unread = 0;
            if (unreadObj != null) unread = (int)GetNumber(unreadObj, "notification_count", 0);
            room.UnreadCount = unread;

            JsonObject summary = GetObject(roomObj, "summary");
            int memberCount = 0;
            if (summary != null) memberCount = (int)GetNumber(summary, "m.joined_member_count", 0);
            if (memberCount == 0) memberCount = _db.CountMembers(roomId);
            room.MemberCount = memberCount;

            if (latestTs > 0)
            {
                room.LastEventTs = latestTs;
                room.LastPreview = preview;
            }

            // Always persist a joined room so it appears in the list, even if this
            // sync window carried no name/message for it (UpsertRoom merges via
            // COALESCE, so null fields never overwrite existing data).
            _db.UpsertRoom(room);

            // If this room was previously a pending invite, joining it clears that state so it
            // stops showing the "Invitation" hint and opens normally.
            _db.SetRoomInvite(roomId, false);

            // Nameless rooms (notably direct messages) have no m.room.name, so without help they'd
            // show as a raw room id. Matrix names such rooms after their "heroes" (the other
            // members the server picks out in the summary). Fill in that fallback only when the room
            // still has no name — SetRoomNameIfEmpty never overwrites a real m.room.name.
            if (string.IsNullOrEmpty(room.Name))
            {
                string heroName = BuildHeroName(roomId, summary);
                if (!string.IsNullOrEmpty(heroName))
                    _db.SetRoomNameIfEmpty(roomId, heroName);
            }

            result.ChangedRooms.Add(roomId);
        }

        /// <summary>
        /// Persists a room the user has been invited to (but not joined). The invite arrives with a
        /// stripped "invite_state" — a small set of state events (name, avatar, topic, and the
        /// m.room.member invite targeting us) rather than a full timeline. We pull a display name
        /// and avatar from it, flag the room as a pending invite, and mark it direct when the invite
        /// says so, so it shows in the list with an Accept/Decline affordance.
        /// </summary>
        private void ProcessInvitedRoom(string roomId, JsonObject roomObj, SyncResult result)
        {
            bool isNew = _db.GetRoom(roomId) == null;
            var room = new Room { Id = roomId };
            string inviter = null;
            bool isDirect = false;

            // The inviter's own member event (membership=join) carries their displayname/avatar, so
            // we collect every member's profile while scanning and resolve the inviter's afterwards.
            var memberNames = new Dictionary<string, string>();
            var memberAvatars = new Dictionary<string, string>();

            JsonObject inviteState = GetObject(roomObj, "invite_state");
            JsonArray events = GetArray(inviteState, "events");
            if (events != null)
            {
                foreach (var evVal in events)
                {
                    JsonObject ev = evVal.GetObject();
                    string type = MatrixClient.GetString(ev, "type");

                    if (type == "m.room.name" || type == "m.room.avatar" || type == "m.room.topic")
                    {
                        ApplyStateEvent(roomId, ev, room);
                    }
                    else if (type == "m.room.member")
                    {
                        // The invite event has our user id as the state_key; its sender is the
                        // person who invited us, and its content may flag the room as a DM.
                        string stateKey = MatrixClient.GetString(ev, "state_key");
                        JsonObject content = GetObject(ev, "content");
                        string membership = content != null ? MatrixClient.GetString(content, "membership") : null;

                        if (content != null && !string.IsNullOrEmpty(stateKey))
                        {
                            string dn = MatrixClient.GetString(content, "displayname");
                            string av = MatrixClient.GetString(content, "avatar_url");
                            if (!string.IsNullOrEmpty(dn)) memberNames[stateKey] = dn;
                            if (!string.IsNullOrEmpty(av)) memberAvatars[stateKey] = av;
                        }

                        if (stateKey == _myUserId && membership == "invite")
                        {
                            inviter = MatrixClient.GetString(ev, "sender");
                            if (content != null && content.ContainsKey("is_direct") &&
                                content["is_direct"].ValueType == JsonValueType.Boolean &&
                                content.GetNamedBoolean("is_direct"))
                            {
                                isDirect = true;
                            }
                        }
                    }
                }
            }

            // Name fallback: a named room keeps its name; otherwise use the inviter's display name,
            // then the localpart of their id.
            if (string.IsNullOrEmpty(room.Name))
            {
                string inviterName = null;
                if (!string.IsNullOrEmpty(inviter)) memberNames.TryGetValue(inviter, out inviterName);
                if (!string.IsNullOrEmpty(inviterName)) room.Name = inviterName;
                else room.Name = !string.IsNullOrEmpty(inviter) ? LocalPart(inviter) : roomId;
            }

            // Avatar fallback: if the room itself has no avatar (typical for a DM), show the
            // inviter's avatar so the invite looks like Element's "X wants to chat" screen.
            if (string.IsNullOrEmpty(room.AvatarMxc) && !string.IsNullOrEmpty(inviter))
            {
                string inviterAvatar;
                if (memberAvatars.TryGetValue(inviter, out inviterAvatar) && !string.IsNullOrEmpty(inviterAvatar))
                    room.AvatarMxc = inviterAvatar;
            }

            // Surface a fresh invite near the top of the list (it has no timeline timestamp).
            if (isNew)
                room.LastEventTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            _db.UpsertRoom(room);
            _db.SetRoomInvite(roomId, true);
            if (isDirect) _db.SetRoomDirect(roomId, true);

            // Record who invited us so the invite screen can show "@inviter:server".
            _db.SetRoomInviter(roomId, inviter);

            App.Log("SYNC invite stored: " + roomId + " from=" + (inviter ?? "<none>") +
                    " isDirect=" + isDirect + " name=" + room.Name +
                    " avatar=" + (string.IsNullOrEmpty(room.AvatarMxc) ? "<none>" : "yes") + " new=" + isNew);

            result.ChangedRooms.Add(roomId);
        }

        /// <summary>
        /// Builds a display name for a nameless room from the summary's m.heroes (the other
        /// members), resolving each to a known display name where possible and otherwise to the
        /// localpart of the Matrix id. Returns null if there are no heroes to name it after.
        /// </summary>
        private string BuildHeroName(string roomId, JsonObject summary)
        {
            JsonArray heroes = GetArray(summary, "m.heroes");
            if (heroes == null || heroes.Count == 0) return null;

            var names = _db.GetMemberNames(roomId);
            var parts = new List<string>();
            foreach (var h in heroes)
            {
                if (h.ValueType != JsonValueType.String) continue;
                string id = h.GetString();
                if (string.IsNullOrEmpty(id) || id == _myUserId) continue;

                string display = null;
                if (names != null) names.TryGetValue(id, out display);
                if (string.IsNullOrEmpty(display)) display = LocalPart(id);
                if (!string.IsNullOrEmpty(display)) parts.Add(display);

                if (parts.Count >= 3) break; // keep the title short
            }

            if (parts.Count == 0) return null;
            return string.Join(", ", parts);
        }

        /// <summary>"@alice:matrix.org" -&gt; "alice"; returns the input unchanged if not a Matrix id.</summary>
        private static string LocalPart(string userId)
        {
            if (string.IsNullOrEmpty(userId)) return userId;
            string s = userId[0] == '@' ? userId.Substring(1) : userId;
            int colon = s.IndexOf(':');
            return colon > 0 ? s.Substring(0, colon) : s;
        }


        /// <summary>Applies a single state event; returns true if room metadata changed.</summary>
        private bool ApplyStateEvent(string roomId, JsonObject ev, Room room, SyncResult result = null, string source = null)
        {
            string type = MatrixClient.GetString(ev, "type");
            JsonObject content = GetObject(ev, "content");
            if (content == null) return false;

            if (IsRtcMemberType(type))
            {
                string sk = MatrixClient.GetString(ev, "state_key");
                if (result != null)
                {
                    var mem = new MatrixRtcMembership
                    {
                        RoomId = roomId,
                        EventId = MatrixClient.GetString(ev, "event_id"),
                        StateKey = sk,
                        UserId = UserFromRtcStateKey(sk),
                        Sender = MatrixClient.GetString(ev, "sender"),
                        Timestamp = (long)GetNumber(ev, "origin_server_ts", 0),
                        Active = IsActiveRtcMembership(content),
                        CallId = MatrixClient.GetString(content, "call_id")
                    };
                    ExtractLiveKitFocus(content, mem);
                    result.MatrixRtcMemberships.Add(mem);
                    // Diagnostic: confirms the membership reached the dispatcher and from which
                    // sync block (a gappy/post-restart sync delivers these in the 'state' block,
                    // a steady long-poll in the 'timeline' block) plus its active flag + age.
                    App.Log("RTC: collected member sk=" + sk + " active=" + mem.Active +
                            " ts=" + mem.Timestamp + " src=" + (source ?? "timeline"));
                }
                else
                {
                    // No result sink (non-RTC-aware call path) -> the membership is silently
                    // dropped and can never ring. Log it so we can spot this in the debug log.
                    App.Log("RTC: member event sk=" + sk + " src=" + (source ?? "?") +
                            " DROPPED (no result sink)");
                }
                return false;
            }

            switch (type)
            {
                case "m.room.name":
                    string name = MatrixClient.GetString(content, "name");
                    if (!string.IsNullOrEmpty(name)) { room.Name = name; return true; }
                    return false;

                case "m.room.topic":
                    string topic = MatrixClient.GetString(content, "topic");
                    if (topic != null) { room.Topic = topic; return true; }
                    return false;

                case "m.room.avatar":
                    string url = MatrixClient.GetString(content, "url");
                    if (!string.IsNullOrEmpty(url)) { room.AvatarMxc = url; return true; }
                    return false;

                case "m.room.member":
                    string userId = MatrixClient.GetString(ev, "state_key");
                    string membership = MatrixClient.GetString(content, "membership");
                    if (!string.IsNullOrEmpty(userId) && membership == "join")
                    {
                        _db.UpsertMember(new Member
                        {
                            RoomId = roomId,
                            UserId = userId,
                            DisplayName = MatrixClient.GetString(content, "displayname"),
                            AvatarMxc = MatrixClient.GetString(content, "avatar_url")
                        });
                    }
                    return false;

                case "m.room.encryption":
                    // The room turned on encryption. Record the algorithm + rotation policy so
                    // outgoing messages get encrypted and the UI can show a lock.
                    string algo = MatrixClient.GetString(content, "algorithm");
                    if (!string.IsNullOrEmpty(algo))
                    {
                        long rotMs = (long)GetNumber(content, "rotation_period_ms", 604800000);
                        int rotMsgs = (int)GetNumber(content, "rotation_period_msgs", 100);
                        _db.SetRoomEncryption(roomId, algo, rotMs, rotMsgs);
                    }
                    return false;
            }
            return false;
        }

        /// <summary>
        /// Persists an m.room.encrypted timeline event as an undecrypted row (msgtype
        /// "m.room.encrypted", body = the ciphertext content JSON) and records it in the sync
        /// result so the sync loop can decrypt it with the CryptoService and overwrite the row.
        /// </summary>
        private EncryptedTimelineEvent ApplyEncryptedEvent(string roomId, JsonObject ev, SyncResult result)
        {
            string eventId = MatrixClient.GetString(ev, "event_id");
            JsonObject content = GetObject(ev, "content");
            if (string.IsNullOrEmpty(eventId) || content == null) return null;

            string sender = MatrixClient.GetString(ev, "sender");
            long ts = (long)GetNumber(ev, "origin_server_ts", 0);

            // Don't clobber an already-decrypted row if this event id was decrypted before.
            var existing = _db.GetMessageById(eventId);
            if (existing == null || existing.MsgType == "m.room.encrypted")
            {
                _db.UpsertMessage(new Message
                {
                    EventId = eventId,
                    RoomId = roomId,
                    Sender = sender,
                    MsgType = "m.room.encrypted",
                    Body = content.Stringify(),
                    Timestamp = ts,
                    Mxc = null,
                    IsLocalEcho = false
                });
            }

            var enc = new EncryptedTimelineEvent
            {
                RoomId = roomId,
                EventId = eventId,
                Sender = sender,
                Timestamp = ts,
                Content = content
            };
            result.EncryptedEvents.Add(enc);
            return enc;
        }

        /// <summary>Persists a timeline message event. Returns the parsed message, or null if skipped.</summary>
        private Message ApplyMessageEvent(string roomId, JsonObject ev)
        {
            string eventId = MatrixClient.GetString(ev, "event_id");
            if (string.IsNullOrEmpty(eventId))
            {
                App.Log("SYNC drop m.room.message room=" + roomId + ": no event_id");
                return null;
            }

            JsonObject content = GetObject(ev, "content");
            if (content == null)
            {
                App.Log("SYNC drop m.room.message id=" + eventId + ": no content (redacted?)");
                return null;
            }

            string msgType = MatrixClient.GetString(content, "msgtype");
            if (msgType != "m.text" && msgType != "m.notice" && msgType != "m.image" && msgType != "m.location")
            {
                // Unsupported message type (file, audio, video, encrypted...). Skip for v1.
                // DIAGNOSTIC: log the actual msgtype so silently-dropped incoming messages are
                // visible. Edits (m.replace), replies and some clients can carry an unexpected or
                // missing msgtype; without this they vanish with no trace.
                bool hasNewContent = content.ContainsKey("m.new_content");
                bool hasRelates = content.ContainsKey("m.relates_to");
                App.Log("SYNC drop m.room.message id=" + eventId + " sender=" +
                        (MatrixClient.GetString(ev, "sender") ?? "?") +
                        " unsupported msgtype=" + (msgType ?? "<null>") +
                        (hasNewContent ? " (edit/m.new_content)" : "") +
                        (hasRelates ? " (has m.relates_to)" : ""));
                return null;
            }

            string sender = MatrixClient.GetString(ev, "sender");
            long ts = (long)GetNumber(ev, "origin_server_ts", 0);
            string body = MatrixClient.GetString(content, "body");
            // For m.location the point lives in geo_uri ("geo:lat,lon"); stash it in mxc (unused for
            // locations) so the message model can render a map, and give the bubble a pin caption.
            string mxc = msgType == "m.image" ? MatrixClient.GetString(content, "url")
                       : msgType == "m.location" ? MatrixClient.GetString(content, "geo_uri")
                       : null;
            if (msgType == "m.location")
                body = "\uD83D\uDCCD " + (string.IsNullOrEmpty(body) ? "Location" : body);

            var msg = new Message
            {
                EventId = eventId,
                RoomId = roomId,
                Sender = sender,
                MsgType = msgType,
                Body = body,
                Timestamp = ts,
                Mxc = mxc,
                IsLocalEcho = false
            };

            // Remove the optimistic local echo for our own just-sent message, if present.
            if (sender == _myUserId)
            {
                RemoveMatchingLocalEcho(roomId, msgType, body);
            }

            _db.UpsertMessage(msg);
            // DIAGNOSTIC: confirm the incoming message was actually persisted (pairs with the "RX"
            // raw-event line and the "SYNC drop" lines so a missed message can be pinpointed to
            // either never-arrived (no RX line), dropped (SYNC drop), or stored-but-not-shown).
            App.Log("SYNC stored msg id=" + eventId + " room=" + roomId + " sender=" +
                    (sender ?? "?") + " type=" + msgType +
                    (sender == _myUserId ? " (own)" : ""));
            return msg;
        }

        /// <summary>
        /// Folds a voice/video call signalling event (m.call.*) into a SINGLE timeline row that
        /// summarises the whole call. All events for one call share a synthetic event id
        /// ("call:" + call_id), so the row is created on the invite and updated in place as the
        /// call is answered and ends — yielding one line per call with an outcome and duration:
        ///   - outgoing connected -> "Outgoing call" (green) + duration
        ///   - incoming answered  -> "Incoming call" (blue) + duration
        ///   - incoming unanswered-> "Missed call"   (red)
        ///   - outgoing unanswered-> "Outgoing call" (green) + "No answer"
        /// The chatty negotiation events (candidates / negotiate / select_answer) are ignored.
        /// Returns the updated marker, or null if the event type isn't surfaced.
        /// </summary>
        private Message ApplyCallEvent(string roomId, string type, JsonObject ev)
        {
            if (type != "m.call.invite" && type != "m.call.answer" &&
                type != "m.call.hangup" && type != "m.call.reject")
                return null; // candidates / negotiate / select_answer don't change the summary

            JsonObject content = GetObject(ev, "content");
            if (content == null) return null;
            string callId = MatrixClient.GetString(content, "call_id");
            if (string.IsNullOrEmpty(callId)) return null;

            string sender = MatrixClient.GetString(ev, "sender");
            long ts = (long)GetNumber(ev, "origin_server_ts", 0);
            bool fromMe = !string.IsNullOrEmpty(_myUserId) && sender == _myUserId;

            // One row per call, keyed by call_id, evolved as the call progresses.
            string syntheticId = "call:" + callId;
            Message msg = _db.GetMessageById(syntheticId);
            if (msg == null)
            {
                msg = new Message
                {
                    EventId = syntheticId,
                    RoomId = roomId,
                    MsgType = "m.call",
                    IsLocalEcho = false
                };
            }

            switch (type)
            {
                case "m.call.invite":
                    // The invite fixes who started the call (direction) and its timeline position.
                    msg.Sender = sender;
                    if (msg.Timestamp == 0) msg.Timestamp = ts;
                    if (msg.CallAnswerTs == 0) // don't downgrade an already-connected call
                        msg.CallKind = fromMe ? "outgoing_noanswer" : "missed";
                    break;

                case "m.call.answer":
                    // The call connected. Direction comes from the invite sender when we have it;
                    // otherwise from the answerer (if I answered it was incoming, else outgoing).
                    msg.CallAnswerTs = ts;
                    bool outgoing = !string.IsNullOrEmpty(msg.Sender)
                        ? (msg.Sender == _myUserId)
                        : !fromMe;
                    msg.CallKind = outgoing ? "outgoing" : "incoming";
                    if (msg.Timestamp == 0) msg.Timestamp = ts;
                    break;

                case "m.call.hangup":
                case "m.call.reject":
                    if (msg.CallAnswerTs > 0)
                    {
                        // Connected call -> show its duration, keep the incoming/outgoing kind.
                        int secs = (int)((ts - msg.CallAnswerTs) / 1000);
                        msg.CallSeconds = secs > 0 ? secs : 0;
                        if (msg.CallKind != "incoming" && msg.CallKind != "outgoing")
                            msg.CallKind = "incoming";
                    }
                    else
                    {
                        // Never answered: incoming -> missed, outgoing -> no answer.
                        bool wasMine = !string.IsNullOrEmpty(msg.Sender)
                            ? (msg.Sender == _myUserId)
                            : fromMe;
                        msg.CallKind = wasMine ? "outgoing_noanswer" : "missed";
                    }
                    if (msg.Timestamp == 0) msg.Timestamp = ts;
                    break;
            }

            msg.Body = CallSummaryLabel(msg.CallKind);
            _db.UpsertMessage(msg);
            return msg;
        }

        // ---- MatrixRTC (Element Call) helpers, shared with the encrypted-decrypt path ----

        /// <summary>True for the MSC4075 RTC ring notification event type (stable + unstable).</summary>
        internal static bool IsRtcNotificationType(string type)
        {
            return type == "m.rtc.notification" || type == "org.matrix.msc4075.rtc.notification";
        }

        /// <summary>True for the MatrixRTC call-membership state event type (current + legacy names).</summary>
        internal static bool IsRtcMemberType(string type)
        {
            return type == "m.rtc.member" ||
                   type == "org.matrix.msc3401.call.member" ||
                   type == "m.call.member";
        }

        /// <summary>
        /// Parses an MSC4075 RTC notification's content into a MatrixRtcNotification. Works for both
        /// the plaintext timeline event and the decrypted inner event (encrypted rooms), so the ring
        /// path is identical regardless of room encryption.
        /// </summary>
        internal static MatrixRtcNotification ParseRtcNotification(string roomId, string eventId, string sender, long ts, JsonObject content)
        {
            if (content == null) return null;
            var n = new MatrixRtcNotification
            {
                RoomId = roomId,
                EventId = eventId,
                Sender = sender,
                Timestamp = ts,
                NotificationType = MatrixClient.GetString(content, "notification_type"),
                Lifetime = (long)GetNumber(content, "lifetime", 0)
            };
            JsonObject rel = GetObject(content, "m.relates_to");
            if (rel != null) n.ParentId = MatrixClient.GetString(rel, "event_id");
            return n;
        }

        /// <summary>
        /// Builds the one-line timeline tile for an incoming MatrixRTC call (rendered like a missed
        /// 1:1 call, since UniMatrix can't join the LiveKit media and the call is taken in Element).
        /// Returns null for our own device's notification (sender == me) so we don't log a self call.
        /// All rings for one call share <paramref name="callKey"/> (the parent event id) so repeats
        /// collapse to a single row.
        /// </summary>
        internal static Message BuildRtcMissedTile(string roomId, string callKey, string sender, long ts, string myUserId)
        {
            if (!string.IsNullOrEmpty(myUserId) && sender == myUserId) return null;
            if (string.IsNullOrEmpty(callKey)) return null;
            return new Message
            {
                EventId = "rtccall:" + callKey,
                RoomId = roomId,
                Sender = sender,
                MsgType = "m.call",
                CallKind = "missed",
                Body = "Missed call",
                Timestamp = ts,
                IsLocalEcho = false
            };
        }

        /// <summary>
        /// Decides whether an m.rtc.member state event means the member is currently in the call.
        /// Empty content (or an empty memberships array) is the standard "left the call" marker.
        /// </summary>
        private static bool IsActiveRtcMembership(JsonObject content)
        {
            if (content == null || content.Keys.Count == 0) return false;
            if (content.ContainsKey("memberships") && content["memberships"].ValueType == JsonValueType.Array)
                return content.GetNamedArray("memberships").Count > 0;
            return true;
        }

        /// <summary>Best-effort user id from an m.rtc.member state key ("@user:server" or "_@user:server_DEVICE").</summary>
        private static string UserFromRtcStateKey(string stateKey)
        {
            if (string.IsNullOrEmpty(stateKey)) return stateKey;
            int at = stateKey.IndexOf('@');
            return at >= 0 ? stateKey.Substring(at) : stateKey;
        }

        /// <summary>
        /// Pulls the LiveKit SFU focus (service URL + room alias) out of an m.rtc.member join content.
        /// Element puts it in foci_preferred[] (each {type:"livekit", livekit_service_url, livekit_alias}).
        /// Sets the membership's FocusServiceUrl/FocusRoomAlias when a LiveKit focus is present.
        /// </summary>
        private static void ExtractLiveKitFocus(JsonObject content, MatrixRtcMembership mem)
        {
            try
            {
                JsonArray foci = GetArray(content, "foci_preferred");
                if (foci == null) return;
                foreach (var fVal in foci)
                {
                    if (fVal.ValueType != JsonValueType.Object) continue;
                    JsonObject f = fVal.GetObject();
                    if (MatrixClient.GetString(f, "type") != "livekit") continue;
                    mem.FocusServiceUrl = MatrixClient.GetString(f, "livekit_service_url");
                    mem.FocusRoomAlias = MatrixClient.GetString(f, "livekit_alias");
                    if (!string.IsNullOrEmpty(mem.FocusServiceUrl)) return;
                }
            }
            catch { }
        }

        /// <summary>Friendly preview/label text for a call row, by its outcome kind.</summary>
        private static string CallSummaryLabel(string callKind)
        {
            switch (callKind)
            {
                case "missed": return "Missed call";
                case "incoming": return "Incoming call";
                case "outgoing":
                case "outgoing_noanswer": return "Outgoing call";
                default: return "Call";
            }
        }

        private void RemoveMatchingLocalEcho(string roomId, string msgType, string body)
        {
            foreach (var m in _db.GetMessages(roomId, 20))
            {
                if (m.IsLocalEcho && m.Sender == _myUserId && m.MsgType == msgType && m.Body == body)
                {
                    _db.DeleteMessage(m.EventId);
                    break;
                }
            }
        }

        private static string BuildPreview(Message msg)
        {
            if (msg.MsgType == "m.image") return "\uD83D\uDCF7 Photo";
            if (msg.MsgType == "m.call") return msg.Body;
            return msg.Body;
        }

        // ---- JSON helpers ----

        /// <summary>
        /// Diagnostic: dumps a raw room event (type, sender, event_id, content) to the debug log so
        /// we can see exactly what the homeserver delivers — including event types UniMatrix doesn't
        /// otherwise surface (e.g. MatrixRTC m.rtc.* events). Content is truncated to keep the log
        /// readable; encrypted ciphertext blobs are noisy but bounded.
        /// </summary>
        internal static void LogRawEvent(string dir, string roomId, JsonObject ev)
        {
            try
            {
                if (ev == null) return;
                string type = MatrixClient.GetString(ev, "type");
                string sender = MatrixClient.GetString(ev, "sender");
                string eventId = MatrixClient.GetString(ev, "event_id");
                string stateKey = MatrixClient.GetString(ev, "state_key");
                JsonObject content = GetObject(ev, "content");
                string c = content != null ? content.Stringify() : "{}";
                if (c != null && c.Length > 900) c = c.Substring(0, 900) + "...(" + c.Length + " chars)";
                App.Log(dir + " " + (roomId ?? "?") + " type=" + (type ?? "?") +
                        " sender=" + (sender ?? "?") +
                        (string.IsNullOrEmpty(stateKey) ? "" : " state_key=" + stateKey) +
                        (string.IsNullOrEmpty(eventId) ? "" : " id=" + eventId) +
                        " content=" + c);
            }
            catch { }
        }

        private static JsonObject GetObject(JsonObject parent, string key)
        {
            try
            {
                if (parent != null && parent.ContainsKey(key) && parent[key].ValueType == JsonValueType.Object)
                    return parent.GetNamedObject(key);
            }
            catch { }
            return null;
        }

        private static JsonArray GetArray(JsonObject parent, string key)
        {
            try
            {
                if (parent != null && parent.ContainsKey(key) && parent[key].ValueType == JsonValueType.Array)
                    return parent.GetNamedArray(key);
            }
            catch { }
            return null;
        }

        private static double GetNumber(JsonObject parent, string key, double fallback)
        {
            try
            {
                if (parent != null && parent.ContainsKey(key) && parent[key].ValueType == JsonValueType.Number)
                    return parent.GetNamedNumber(key);
            }
            catch { }
            return fallback;
        }
    }
}
