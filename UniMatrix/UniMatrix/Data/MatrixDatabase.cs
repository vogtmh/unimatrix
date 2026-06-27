using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using UniMatrix.Models;

namespace UniMatrix.Data
{
    /// <summary>
    /// SQLite cache for Matrix chat data: rooms, message timelines, members,
    /// downloaded media paths, and sync state (the next_batch token).
    /// The end-to-end-encryption key store (Olm/Megolm sessions, device keys, etc.) lives in
    /// the partial in MatrixDatabase.Crypto.cs.
    /// </summary>
    internal partial class MatrixDatabase : IDisposable
    {
        private SqliteConnection _connection;
        private readonly string _dbPath;
        // Serializes all access to the single SQLite connection. The sync processor,
        // the background history backfill, and UI-thread reads can all touch the
        // connection concurrently, and SqliteConnection is not thread-safe.
        private readonly object _gate = new object();

        public string DbPath => _dbPath;

        public MatrixDatabase(string dbPath)
        {
            _dbPath = dbPath;
        }

        public async Task OpenAsync()
        {
            await Task.Run(() =>
            {
                _connection = new SqliteConnection($"Data Source={_dbPath}");
                _connection.Open();

                Execute("PRAGMA journal_mode=WAL");
                Execute("PRAGMA synchronous=NORMAL"); // safe with WAL; OFF risks corruption on OS crash
                Execute("PRAGMA cache_size=-16000"); // 16MB cache
                Execute("PRAGMA temp_store=MEMORY");
                Execute("PRAGMA page_size=4096");
            });
        }

        public void CreateSchema()
        {
            Execute(@"
                CREATE TABLE IF NOT EXISTS rooms (
                    id            TEXT PRIMARY KEY,
                    name          TEXT,
                    topic         TEXT,
                    avatar_mxc    TEXT,
                    member_count  INTEGER NOT NULL DEFAULT 0,
                    unread        INTEGER NOT NULL DEFAULT 0,
                    last_ts       INTEGER NOT NULL DEFAULT 0,
                    last_preview  TEXT
                )");

            // Migration: add the direct-message flag to pre-existing databases. ALTER TABLE ADD
            // COLUMN throws "duplicate column" once the column exists, so this is a no-op after the
            // first run; swallow that case.
            try { Execute("ALTER TABLE rooms ADD COLUMN is_direct INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already present */ }

            // Migration: add the pending-invite flag (room the user was invited to but hasn't
            // joined). Same no-op-after-first-run pattern as is_direct above.
            try { Execute("ALTER TABLE rooms ADD COLUMN is_invite INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already present */ }

            // Migration: remember who invited the user (the inviter's @user:server), shown on the
            // invite screen. Same no-op-after-first-run pattern.
            try { Execute("ALTER TABLE rooms ADD COLUMN inviter TEXT"); }
            catch { /* column already present */ }

            Execute(@"
                CREATE TABLE IF NOT EXISTS messages (
                    event_id   TEXT PRIMARY KEY,
                    room_id    TEXT NOT NULL,
                    sender     TEXT,
                    msgtype    TEXT,
                    body       TEXT,
                    ts         INTEGER NOT NULL DEFAULT 0,
                    mxc        TEXT,
                    local_echo INTEGER NOT NULL DEFAULT 0
                )");

            Execute("CREATE INDEX IF NOT EXISTS idx_messages_room_ts ON messages(room_id, ts)");

            // Migration: call-summary columns (one timeline row per voice call). Same
            // no-op-after-first-run ALTER pattern as the rooms columns above.
            try { Execute("ALTER TABLE messages ADD COLUMN call_kind TEXT"); }
            catch { /* column already present */ }
            try { Execute("ALTER TABLE messages ADD COLUMN call_seconds INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already present */ }
            try { Execute("ALTER TABLE messages ADD COLUMN call_answer_ts INTEGER NOT NULL DEFAULT 0"); }
            catch { /* column already present */ }

            Execute(@"
                CREATE TABLE IF NOT EXISTS members (
                    room_id      TEXT NOT NULL,
                    user_id      TEXT NOT NULL,
                    display_name TEXT,
                    avatar_mxc   TEXT,
                    PRIMARY KEY (room_id, user_id)
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS media_cache (
                    mxc        TEXT PRIMARY KEY,
                    local_path TEXT NOT NULL,
                    fetched_ts INTEGER NOT NULL DEFAULT 0
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS meta (
                    key   TEXT PRIMARY KEY,
                    value TEXT
                )");

            CreateCryptoSchema();
        }

        // ---- Meta (sync token, identity) ----

        public string GetMeta(string key)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SetMeta(string key, string value)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES(@k, @v)";
                cmd.Parameters.AddWithValue("@k", key);
                cmd.Parameters.AddWithValue("@v", (object)value ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Rooms ----

        public void UpsertRoom(Room room)
        {
            // The system winsqlite3 on Windows 10 Mobile predates UPSERT
            // (INSERT ... ON CONFLICT DO UPDATE, added in SQLite 3.24.0), so we use a
            // portable insert-then-update. INSERT OR IGNORE creates the row if missing;
            // the UPDATE then merges, preserving existing name/avatar/topic when the
            // incoming value is null so state-light sync updates don't wipe details.
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR IGNORE INTO rooms(id, name, topic, avatar_mxc, member_count, unread, last_ts, last_preview)
                    VALUES(@id, @name, @topic, @avatar, @members, @unread, @ts, @preview)";
                AddRoomParameters(cmd, room);
                cmd.ExecuteNonQuery();
            }

            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    UPDATE rooms SET
                        name         = COALESCE(@name, name),
                        topic        = COALESCE(@topic, topic),
                        avatar_mxc   = COALESCE(@avatar, avatar_mxc),
                        member_count = CASE WHEN @members > 0 THEN @members ELSE member_count END,
                        unread       = @unread,
                        last_ts      = CASE WHEN @ts > 0 THEN @ts ELSE last_ts END,
                        last_preview = COALESCE(@preview, last_preview)
                    WHERE id = @id";
                AddRoomParameters(cmd, room);
                cmd.ExecuteNonQuery();
            }
        }

        private static void AddRoomParameters(SqliteCommand cmd, Room room)
        {
            cmd.Parameters.AddWithValue("@id", room.Id);
            cmd.Parameters.AddWithValue("@name", (object)room.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@topic", (object)room.Topic ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avatar", (object)room.AvatarMxc ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@members", room.MemberCount);
            cmd.Parameters.AddWithValue("@unread", room.UnreadCount);
            cmd.Parameters.AddWithValue("@ts", room.LastEventTs);
            cmd.Parameters.AddWithValue("@preview", (object)room.LastPreview ?? DBNull.Value);
        }

        public void SetRoomUnread(string roomId, int unread)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET unread = @u WHERE id = @id";
                cmd.Parameters.AddWithValue("@u", unread);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Updates a room's last-message preview, but only when the supplied timestamp is at least
        /// as new as the stored last_ts. Used after an encrypted event is decrypted: the encrypted
        /// placeholder preview ("Encrypted message") is replaced with the real text, while older
        /// messages that decrypt later (retry path) never clobber a newer preview.
        /// </summary>
        public void SetRoomPreviewIfLatest(string roomId, long ts, string preview)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET last_preview = @p WHERE id = @id AND last_ts <= @ts";
                cmd.Parameters.AddWithValue("@p", (object)preview ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", ts);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Sets a room's name ONLY when it doesn't already have one. Used to give nameless rooms
        /// (notably direct messages) a sensible fallback derived from their members, without ever
        /// overwriting a real m.room.name that was set by the room.
        /// </summary>
        public void SetRoomNameIfEmpty(string roomId, string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET name = @name WHERE id = @id AND (name IS NULL OR name = '')";
                cmd.Parameters.AddWithValue("@name", name);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        public List<Room> GetRooms()
        {
            var result = new List<Room>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, name, topic, avatar_mxc, member_count, unread, last_ts, last_preview, is_direct, is_invite, inviter
                                    FROM rooms ORDER BY last_ts DESC";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        result.Add(new Room
                        {
                            Id = r.GetString(0),
                            Name = r.IsDBNull(1) ? null : r.GetString(1),
                            Topic = r.IsDBNull(2) ? null : r.GetString(2),
                            AvatarMxc = r.IsDBNull(3) ? null : r.GetString(3),
                            MemberCount = r.GetInt32(4),
                            UnreadCount = r.GetInt32(5),
                            LastEventTs = r.GetInt64(6),
                            LastPreview = r.IsDBNull(7) ? null : r.GetString(7),
                            IsDirect = !r.IsDBNull(8) && r.GetInt32(8) != 0,
                            IsInvite = !r.IsDBNull(9) && r.GetInt32(9) != 0,
                            Inviter = r.IsDBNull(10) ? null : r.GetString(10)
                        });
                    }
                }
            }
            return result;
        }

        /// <summary>Returns a single room by id, or null if it isn't cached.</summary>
        public Room GetRoom(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, name, topic, avatar_mxc, member_count, unread, last_ts, last_preview, is_direct, is_invite, inviter
                                    FROM rooms WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", roomId);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        return new Room
                        {
                            Id = r.GetString(0),
                            Name = r.IsDBNull(1) ? null : r.GetString(1),
                            Topic = r.IsDBNull(2) ? null : r.GetString(2),
                            AvatarMxc = r.IsDBNull(3) ? null : r.GetString(3),
                            MemberCount = r.GetInt32(4),
                            UnreadCount = r.GetInt32(5),
                            LastEventTs = r.GetInt64(6),
                            LastPreview = r.IsDBNull(7) ? null : r.GetString(7),
                            IsDirect = !r.IsDBNull(8) && r.GetInt32(8) != 0,
                            IsInvite = !r.IsDBNull(9) && r.GetInt32(9) != 0,
                            Inviter = r.IsDBNull(10) ? null : r.GetString(10)
                        };
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Flags (or unflags) a room as a direct message. Driven by the m.direct account-data map,
        /// which lists every room the user treats as a 1:1 chat. Stored separately from UpsertRoom
        /// (whose COALESCE merge would complicate a boolean) so it's never accidentally cleared by a
        /// state-light sync update.
        /// </summary>
        public void SetRoomDirect(string roomId, bool isDirect)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET is_direct = @d WHERE id = @id";
                cmd.Parameters.AddWithValue("@d", isDirect ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Flags (or clears) a room as a pending invite. Set when the room arrives in the /sync
        /// "invite" section, cleared once the user joins (the room then arrives under "join").
        /// Stored separately from UpsertRoom for the same reason as is_direct.
        /// </summary>
        public void SetRoomInvite(string roomId, bool isInvite)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET is_invite = @i WHERE id = @id";
                cmd.Parameters.AddWithValue("@i", isInvite ? 1 : 0);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Records who invited the user to a room (the inviter's @user:server), shown on the invite
        /// screen. A null/empty value clears it. Stored separately from UpsertRoom like is_invite.
        /// </summary>
        public void SetRoomInviter(string roomId, string inviter)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET inviter = @inv WHERE id = @id";
                cmd.Parameters.AddWithValue("@inv", (object)inviter ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Messages ----

        public void UpsertMessage(Message m)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO messages(event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts)
                    VALUES(@id, @room, @sender, @type, @body, @ts, @mxc, @echo, @callkind, @callsecs, @callans)";
                cmd.Parameters.AddWithValue("@id", m.EventId);
                cmd.Parameters.AddWithValue("@room", m.RoomId);
                cmd.Parameters.AddWithValue("@sender", (object)m.Sender ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@type", (object)m.MsgType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@body", (object)m.Body ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", m.Timestamp);
                cmd.Parameters.AddWithValue("@mxc", (object)m.Mxc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@echo", m.IsLocalEcho ? 1 : 0);
                cmd.Parameters.AddWithValue("@callkind", (object)m.CallKind ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@callsecs", m.CallSeconds);
                cmd.Parameters.AddWithValue("@callans", m.CallAnswerTs);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Removes a local-echo placeholder once the real event arrives via sync.</summary>
        public void DeleteMessage(string eventId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM messages WHERE event_id = @id";
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Removes a room and all of its cached messages and members (e.g. on leave).</summary>
        public void DeleteRoom(string roomId)
        {
            lock (_gate)
            {
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"DELETE FROM messages WHERE room_id = @room;
                                        DELETE FROM members  WHERE room_id = @room;
                                        DELETE FROM rooms    WHERE id      = @room;
                                        DELETE FROM meta     WHERE key = 'bf_token_' || @room
                                                                OR key = 'bf_done_'  || @room;";
                    cmd.Parameters.AddWithValue("@room", roomId);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool MessageExists(string eventId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE event_id = @id";
                cmd.Parameters.AddWithValue("@id", eventId);
                return (long)cmd.ExecuteScalar() > 0;
            }
        }

        /// <summary>Returns a single stored message by its event id, or null if none exists.</summary>
        public Message GetMessageById(string eventId)
        {
            var result = new List<Message>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                    FROM messages WHERE event_id = @id LIMIT 1";
                cmd.Parameters.AddWithValue("@id", eventId);
                ReadMessages(cmd, result);
            }
            return result.Count > 0 ? result[0] : null;
        }

        /// <summary>Returns the most recent messages for a room in chronological (oldest-first) order.</summary>
        public List<Message> GetMessages(string roomId, int limit)        {
            var result = new List<Message>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                    FROM messages WHERE room_id = @room
                                    ORDER BY ts DESC LIMIT @limit";
                cmd.Parameters.AddWithValue("@room", roomId);
                cmd.Parameters.AddWithValue("@limit", limit);
                ReadMessages(cmd, result);
            }
            // Stored newest-first for the LIMIT; reverse to oldest-first for display.
            result.Reverse();
            return result;
        }

        /// <summary>Oldest stored message timestamp for a room, or 0 if the room has none.</summary>
        public long GetOldestMessageTs(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MIN(ts) FROM messages WHERE room_id = @room";
                cmd.Parameters.AddWithValue("@room", roomId);
                var v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return 0;
                return Convert.ToInt64(v);
            }
        }

        /// <summary>
        /// Returns the event id of the newest real (non-local-echo) message in a room, or null if
        /// the room has no confirmed messages. Used to send a read receipt up to the latest event.
        /// </summary>
        public string GetLatestRealEventId(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id FROM messages
                                    WHERE room_id = @room AND local_echo = 0
                                      AND event_id NOT LIKE 'call:%'
                                    ORDER BY ts DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@room", roomId);
                return cmd.ExecuteScalar() as string;
            }
        }

        /// <summary>
        /// Returns the newest real (non-echo) message in a room that was NOT sent by
        /// <paramref name="myUserId"/>, or null if there is none. Used by the notification task so
        /// we only ever notify about messages from other people, never our own.
        /// </summary>
        public Message GetLatestIncomingMessage(string roomId, string myUserId)
        {
            var result = new List<Message>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                    FROM messages
                                    WHERE room_id = @room AND local_echo = 0
                                      AND (sender IS NULL OR sender <> @me)
                                    ORDER BY ts DESC LIMIT 1";
                cmd.Parameters.AddWithValue("@room", roomId);
                cmd.Parameters.AddWithValue("@me", (object)myUserId ?? "");
                ReadMessages(cmd, result);
            }
            return result.Count > 0 ? result[0] : null;
        }

        /// <summary>Newest stored message timestamp for a room (0 if none). Diagnostics.</summary>
        public long GetNewestMessageTs(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT MAX(ts) FROM messages WHERE room_id = @room";
                cmd.Parameters.AddWithValue("@room", roomId);
                var v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return 0;
                return Convert.ToInt64(v);
            }
        }

        /// <summary>Total stored messages for a room. Diagnostics.</summary>
        public int CountMessages(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE room_id = @room";
                cmd.Parameters.AddWithValue("@room", roomId);
                var v = cmd.ExecuteScalar();
                if (v == null || v == DBNull.Value) return 0;
                return Convert.ToInt32(v);
            }
        }

        /// <summary>
        /// Returns all messages with a timestamp at or after <paramref name="sinceTs"/>
        /// (oldest-first). If the room had no activity in that window, falls back to the most
        /// recent <paramref name="fallbackCount"/> messages so the chat is never empty
        /// (e.g. quiet rooms whose last message is older than the window).
        /// </summary>
        public List<Message> GetMessagesSince(string roomId, long sinceTs, int fallbackCount)
        {
            var result = new List<Message>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                    FROM messages WHERE room_id = @room AND ts >= @since
                                    ORDER BY ts DESC";
                cmd.Parameters.AddWithValue("@room", roomId);
                cmd.Parameters.AddWithValue("@since", sinceTs);
                ReadMessages(cmd, result);
            }

            if (result.Count == 0)
            {
                lock (_gate)
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                        FROM messages WHERE room_id = @room
                                        ORDER BY ts DESC LIMIT @limit";
                    cmd.Parameters.AddWithValue("@room", roomId);
                    cmd.Parameters.AddWithValue("@limit", fallbackCount);
                    ReadMessages(cmd, result);
                }
            }

            // Stored newest-first; reverse to oldest-first for display.
            result.Reverse();
            return result;
        }

        /// <summary>
        /// Returns up to <paramref name="limit"/> messages strictly older than the cursor
        /// (<paramref name="beforeTs"/>, <paramref name="beforeEventId"/>), oldest-first. Used
        /// for scroll-back lazy loading so only a small page is held in memory at a time. The
        /// compound (ts, event_id) cursor is stable even when several messages share a timestamp.
        /// </summary>
        public List<Message> GetMessagesBefore(string roomId, long beforeTs, string beforeEventId, int limit)
        {
            var result = new List<Message>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo, call_kind, call_seconds, call_answer_ts
                                    FROM messages
                                    WHERE room_id = @room
                                      AND (ts < @ts OR (ts = @ts AND event_id < @id))
                                    ORDER BY ts DESC, event_id DESC
                                    LIMIT @limit";
                cmd.Parameters.AddWithValue("@room", roomId);
                cmd.Parameters.AddWithValue("@ts", beforeTs);
                cmd.Parameters.AddWithValue("@id", (object)beforeEventId ?? "");
                cmd.Parameters.AddWithValue("@limit", limit);
                ReadMessages(cmd, result);
            }
            // Stored newest-first for the LIMIT; reverse to oldest-first for display.
            result.Reverse();
            return result;
        }

        private static void ReadMessages(SqliteCommand cmd, List<Message> result)
        {
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    result.Add(new Message
                    {
                        EventId = r.GetString(0),
                        RoomId = r.GetString(1),
                        Sender = r.IsDBNull(2) ? null : r.GetString(2),
                        MsgType = r.IsDBNull(3) ? null : r.GetString(3),
                        Body = r.IsDBNull(4) ? null : r.GetString(4),
                        Timestamp = r.GetInt64(5),
                        Mxc = r.IsDBNull(6) ? null : r.GetString(6),
                        IsLocalEcho = r.GetInt32(7) != 0,
                        CallKind = r.IsDBNull(8) ? null : r.GetString(8),
                        CallSeconds = r.GetInt32(9),
                        CallAnswerTs = r.GetInt64(10)
                    });
                }
            }
        }

        // ---- Members ----

        public void UpsertMember(Member m)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO members(room_id, user_id, display_name, avatar_mxc)
                    VALUES(@room, @user, @name, @avatar)";
                cmd.Parameters.AddWithValue("@room", m.RoomId);
                cmd.Parameters.AddWithValue("@user", m.UserId);
                cmd.Parameters.AddWithValue("@name", (object)m.DisplayName ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@avatar", (object)m.AvatarMxc ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public Dictionary<string, string> GetMemberNames(string roomId)
        {
            var map = new Dictionary<string, string>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT user_id, display_name FROM members WHERE room_id = @room";
                cmd.Parameters.AddWithValue("@room", roomId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        if (!r.IsDBNull(1))
                            map[r.GetString(0)] = r.GetString(1);
                    }
                }
            }
            return map;
        }

        public int CountMembers(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM members WHERE room_id = @room";
                cmd.Parameters.AddWithValue("@room", roomId);
                return (int)(long)cmd.ExecuteScalar();
            }
        }

        // ---- Media cache ----

        public string GetCachedMedia(string mxc)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT local_path FROM media_cache WHERE mxc = @mxc";
                cmd.Parameters.AddWithValue("@mxc", mxc);
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SetCachedMedia(string mxc, string localPath)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"INSERT OR REPLACE INTO media_cache(mxc, local_path, fetched_ts)
                                    VALUES(@mxc, @path, @ts)";
                cmd.Parameters.AddWithValue("@mxc", mxc);
                cmd.Parameters.AddWithValue("@path", localPath);
                cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Maintenance ----

        public SqliteTransaction BeginTransaction()
        {
            return _connection.BeginTransaction();
        }

        /// <summary>Flushes the write-ahead log into the main database file.</summary>
        public void Checkpoint()
        {
            lock (_gate)
            {
                try { Execute("PRAGMA wal_checkpoint(TRUNCATE)"); }
                catch { }
            }
        }

        /// <summary>
        /// Drops all cached chat data (messages/rooms/members/media/meta). Used by both logout and
        /// the "wipe cache &amp; resync" action, so it deliberately does NOT touch the E2EE tables:
        /// the Olm identity and room keys must survive a cache wipe (otherwise the user could no
        /// longer read their encrypted history). Logout wipes the crypto tables separately via
        /// <see cref="ClearCryptoTables"/>.
        /// </summary>
        public void ClearAll()
        {
            lock (_gate)
            {
                Execute("DELETE FROM messages");
                Execute("DELETE FROM rooms");
                Execute("DELETE FROM members");
                Execute("DELETE FROM media_cache");
                Execute("DELETE FROM meta");
                Checkpoint();
            }
        }

        private void Execute(string sql)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.ExecuteNonQuery();
            }
        }

        public void Dispose()
        {
            try { Checkpoint(); } catch { }
            _connection?.Dispose();
            _connection = null;
        }
    }
}
