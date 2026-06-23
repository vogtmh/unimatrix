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
    /// </summary>
    internal class MatrixDatabase : IDisposable
    {
        private SqliteConnection _connection;
        private readonly string _dbPath;

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
        }

        // ---- Meta (sync token, identity) ----

        public string GetMeta(string key)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT value FROM meta WHERE key = @k";
                cmd.Parameters.AddWithValue("@k", key);
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SetMeta(string key, string value)
        {
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
            using (var cmd = _connection.CreateCommand())
            {
                // Preserve existing name/avatar/topic when the new value is null so that
                // state-light sync updates don't wipe previously known details.
                cmd.CommandText = @"
                    INSERT INTO rooms(id, name, topic, avatar_mxc, member_count, unread, last_ts, last_preview)
                    VALUES(@id, @name, @topic, @avatar, @members, @unread, @ts, @preview)
                    ON CONFLICT(id) DO UPDATE SET
                        name         = COALESCE(@name, name),
                        topic        = COALESCE(@topic, topic),
                        avatar_mxc   = COALESCE(@avatar, avatar_mxc),
                        member_count = CASE WHEN @members > 0 THEN @members ELSE member_count END,
                        unread       = @unread,
                        last_ts      = CASE WHEN @ts > 0 THEN @ts ELSE last_ts END,
                        last_preview = COALESCE(@preview, last_preview)";
                cmd.Parameters.AddWithValue("@id", room.Id);
                cmd.Parameters.AddWithValue("@name", (object)room.Name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@topic", (object)room.Topic ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@avatar", (object)room.AvatarMxc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@members", room.MemberCount);
                cmd.Parameters.AddWithValue("@unread", room.UnreadCount);
                cmd.Parameters.AddWithValue("@ts", room.LastEventTs);
                cmd.Parameters.AddWithValue("@preview", (object)room.LastPreview ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void SetRoomUnread(string roomId, int unread)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE rooms SET unread = @u WHERE id = @id";
                cmd.Parameters.AddWithValue("@u", unread);
                cmd.Parameters.AddWithValue("@id", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        public List<Room> GetRooms()
        {
            var result = new List<Room>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT id, name, topic, avatar_mxc, member_count, unread, last_ts, last_preview
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
                            LastPreview = r.IsDBNull(7) ? null : r.GetString(7)
                        });
                    }
                }
            }
            return result;
        }

        // ---- Messages ----

        public void UpsertMessage(Message m)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO messages(event_id, room_id, sender, msgtype, body, ts, mxc, local_echo)
                    VALUES(@id, @room, @sender, @type, @body, @ts, @mxc, @echo)";
                cmd.Parameters.AddWithValue("@id", m.EventId);
                cmd.Parameters.AddWithValue("@room", m.RoomId);
                cmd.Parameters.AddWithValue("@sender", (object)m.Sender ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@type", (object)m.MsgType ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@body", (object)m.Body ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", m.Timestamp);
                cmd.Parameters.AddWithValue("@mxc", (object)m.Mxc ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@echo", m.IsLocalEcho ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>Removes a local-echo placeholder once the real event arrives via sync.</summary>
        public void DeleteMessage(string eventId)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM messages WHERE event_id = @id";
                cmd.Parameters.AddWithValue("@id", eventId);
                cmd.ExecuteNonQuery();
            }
        }

        public bool MessageExists(string eventId)
        {
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM messages WHERE event_id = @id";
                cmd.Parameters.AddWithValue("@id", eventId);
                return (long)cmd.ExecuteScalar() > 0;
            }
        }

        /// <summary>Returns the most recent messages for a room in chronological (oldest-first) order.</summary>
        public List<Message> GetMessages(string roomId, int limit)
        {
            var result = new List<Message>();
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, room_id, sender, msgtype, body, ts, mxc, local_echo
                                    FROM messages WHERE room_id = @room
                                    ORDER BY ts DESC LIMIT @limit";
                cmd.Parameters.AddWithValue("@room", roomId);
                cmd.Parameters.AddWithValue("@limit", limit);
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
                            IsLocalEcho = r.GetInt32(7) != 0
                        });
                    }
                }
            }
            // Stored newest-first for the LIMIT; reverse to oldest-first for display.
            result.Reverse();
            return result;
        }

        // ---- Members ----

        public void UpsertMember(Member m)
        {
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
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT local_path FROM media_cache WHERE mxc = @mxc";
                cmd.Parameters.AddWithValue("@mxc", mxc);
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SetCachedMedia(string mxc, string localPath)
        {
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
            try { Execute("PRAGMA wal_checkpoint(TRUNCATE)"); }
            catch { }
        }

        /// <summary>Drops all cached data on logout.</summary>
        public void ClearAll()
        {
            Execute("DELETE FROM messages");
            Execute("DELETE FROM rooms");
            Execute("DELETE FROM members");
            Execute("DELETE FROM media_cache");
            Execute("DELETE FROM meta");
            Checkpoint();
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
