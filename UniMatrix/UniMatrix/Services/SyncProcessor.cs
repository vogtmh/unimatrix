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

            JsonObject rooms = GetObject(sync, "rooms");
            if (rooms == null) return result;

            JsonObject join = GetObject(rooms, "join");
            if (join == null) return result;

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

            return result;
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
                        ApplyStateEvent(roomId, ev.GetObject(), room);
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
                if (events != null)
                {
                    foreach (var evVal in events)
                    {
                        JsonObject ev = evVal.GetObject();
                        string type = MatrixClient.GetString(ev, "type");

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
                        else if (type == "m.room.name" || type == "m.room.avatar" ||
                                 type == "m.room.topic" || type == "m.room.member")
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
        private bool ApplyStateEvent(string roomId, JsonObject ev, Room room)
        {
            string type = MatrixClient.GetString(ev, "type");
            JsonObject content = GetObject(ev, "content");
            if (content == null) return false;

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
            }
            return false;
        }

        /// <summary>Persists a timeline message event. Returns the parsed message, or null if skipped.</summary>
        private Message ApplyMessageEvent(string roomId, JsonObject ev)
        {
            string eventId = MatrixClient.GetString(ev, "event_id");
            if (string.IsNullOrEmpty(eventId)) return null;

            JsonObject content = GetObject(ev, "content");
            if (content == null) return null;

            string msgType = MatrixClient.GetString(content, "msgtype");
            if (msgType != "m.text" && msgType != "m.notice" && msgType != "m.image")
            {
                // Unsupported message type (file, audio, video, encrypted...). Skip for v1.
                return null;
            }

            string sender = MatrixClient.GetString(ev, "sender");
            long ts = (long)GetNumber(ev, "origin_server_ts", 0);
            string body = MatrixClient.GetString(content, "body");
            string mxc = msgType == "m.image" ? MatrixClient.GetString(content, "url") : null;

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
            return msg;
        }

        /// <summary>
        /// Turns a voice/video call signaling event (m.call.*) into a single timeline
        /// marker so the user can see that a call happened. Only the call invite is
        /// surfaced; the chatty negotiation events (candidates, answer, negotiate,
        /// select_answer) are skipped so one call yields at most one timeline line.
        /// Returns the parsed marker, or null if the event type is not surfaced.
        /// </summary>
        private Message ApplyCallEvent(string roomId, string type, JsonObject ev)
        {
            string label;
            switch (type)
            {
                case "m.call.invite": label = "\uD83D\uDCDE Call"; break;
                case "m.call.reject": label = "\uD83D\uDCDE Call declined"; break;
                default: return null; // candidates / answer / hangup / negotiate / select_answer
            }

            string eventId = MatrixClient.GetString(ev, "event_id");
            if (string.IsNullOrEmpty(eventId)) return null;

            var msg = new Message
            {
                EventId = eventId,
                RoomId = roomId,
                Sender = MatrixClient.GetString(ev, "sender"),
                MsgType = "m.call",
                Body = label,
                Timestamp = (long)GetNumber(ev, "origin_server_ts", 0),
                IsLocalEcho = false
            };
            _db.UpsertMessage(msg);
            return msg;
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
