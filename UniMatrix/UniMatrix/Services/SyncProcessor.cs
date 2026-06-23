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
            result.ChangedRooms.Add(roomId);
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
                RemoveMatchingLocalEcho(roomId, body);
            }

            _db.UpsertMessage(msg);
            return msg;
        }

        /// <summary>
        /// Deletes a pending local-echo placeholder that matches a confirmed message
        /// of ours, preventing a brief duplicate after sending.
        /// </summary>
        private void RemoveMatchingLocalEcho(string roomId, string body)
        {
            foreach (var m in _db.GetMessages(roomId, 20))
            {
                if (m.IsLocalEcho && m.Sender == _myUserId && m.Body == body)
                {
                    _db.DeleteMessage(m.EventId);
                    break;
                }
            }
        }

        private static string BuildPreview(Message msg)
        {
            if (msg.MsgType == "m.image") return "\uD83D\uDCF7 Photo";
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
