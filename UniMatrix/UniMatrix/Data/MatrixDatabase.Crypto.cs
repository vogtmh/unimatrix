using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace UniMatrix.Data
{
    /// <summary>A stored 1:1 Olm session pickle, keyed by the peer's Curve25519 key.</summary>
    internal class StoredOlmSession
    {
        public string SessionId;
        public string Pickle;
        public long LastUsed;
    }

    /// <summary>A stored inbound Megolm session (decrypts one sender's room messages).</summary>
    internal class StoredInboundGroupSession
    {
        public string RoomId;
        public string SenderKey;
        public string SessionId;
        public string Pickle;
        public string Ed25519;
        public bool Forwarded;
    }

    /// <summary>A stored outbound Megolm session (encrypts our messages in one room).</summary>
    internal class StoredOutboundGroupSession
    {
        public string RoomId;
        public string Pickle;
        public string SessionId;
        public long CreatedTs;
        public int MsgCount;
        public string SharedTo; // JSON array of "userId|deviceId" entries already keyed
    }

    /// <summary>A stored remote device's published keys.</summary>
    internal class StoredDevice
    {
        public string UserId;
        public string DeviceId;
        public string Curve25519;
        public string Ed25519;
        public string Json;   // the full device_keys object (for re-verification / display)
        public int Trust;     // 0 = unverified (TOFU), 1 = verified (reserved for future use)
    }

    /// <summary>
    /// End-to-end-encryption key store: the Olm account, Olm/Megolm sessions, tracked device
    /// keys and per-room encryption state. All methods lock the shared connection gate, like the
    /// rest of MatrixDatabase (the single SqliteConnection is not thread-safe and the crypto
    /// service is driven from the background sync loop as well as the UI thread).
    /// </summary>
    internal partial class MatrixDatabase
    {
        private void CreateCryptoSchema()
        {
            // The pickled Olm account is a single row (id is pinned to 1).
            Execute(@"
                CREATE TABLE IF NOT EXISTS crypto_account (
                    id          INTEGER PRIMARY KEY CHECK (id = 1),
                    pickle      TEXT,
                    device_id   TEXT,
                    ed25519     TEXT,
                    curve25519  TEXT
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS olm_sessions (
                    sender_key TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    pickle     TEXT NOT NULL,
                    last_used  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (sender_key, session_id)
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS megolm_in (
                    room_id    TEXT NOT NULL,
                    sender_key TEXT NOT NULL,
                    session_id TEXT NOT NULL,
                    pickle     TEXT NOT NULL,
                    ed25519    TEXT,
                    forwarded  INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (room_id, sender_key, session_id)
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS megolm_out (
                    room_id    TEXT PRIMARY KEY,
                    pickle     TEXT NOT NULL,
                    session_id TEXT,
                    created_ts INTEGER NOT NULL DEFAULT 0,
                    msg_count  INTEGER NOT NULL DEFAULT 0,
                    shared_to  TEXT
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS device_keys (
                    user_id    TEXT NOT NULL,
                    device_id  TEXT NOT NULL,
                    curve25519 TEXT,
                    ed25519    TEXT,
                    json       TEXT,
                    trust      INTEGER NOT NULL DEFAULT 0,
                    PRIMARY KEY (user_id, device_id)
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS tracked_users (
                    user_id TEXT PRIMARY KEY,
                    dirty   INTEGER NOT NULL DEFAULT 1
                )");

            Execute(@"
                CREATE TABLE IF NOT EXISTS e2ee_rooms (
                    room_id       TEXT PRIMARY KEY,
                    algorithm     TEXT,
                    rotation_ms   INTEGER NOT NULL DEFAULT 604800000,
                    rotation_msgs INTEGER NOT NULL DEFAULT 100
                )");

            // The per-attachment AES key/iv/hash ("file" block), keyed by the uploaded mxc, so the
            // media layer can decrypt an encrypted image when it later downloads it (the timeline
            // row only retains the mxc, not the key material).
            Execute(@"
                CREATE TABLE IF NOT EXISTS attachment_keys (
                    mxc       TEXT PRIMARY KEY,
                    file_json TEXT NOT NULL
                )");
        }

        /// <summary>Wipes all E2EE state. Called on logout (NOT on cache-wipe).</summary>
        public void ClearCryptoTables()
        {
            lock (_gate)
            {
                Execute("DELETE FROM crypto_account");
                Execute("DELETE FROM olm_sessions");
                Execute("DELETE FROM megolm_in");
                Execute("DELETE FROM megolm_out");
                Execute("DELETE FROM device_keys");
                Execute("DELETE FROM tracked_users");
                Execute("DELETE FROM e2ee_rooms");
                Execute("DELETE FROM attachment_keys");
                Execute("DELETE FROM meta WHERE key IN ('backup_version','backup_pubkey','backup_private_key')");
            }
        }

        // ---- Encrypted attachment keys ----

        public void SaveAttachmentKey(string mxc, string fileJson)
        {
            if (string.IsNullOrEmpty(mxc) || string.IsNullOrEmpty(fileJson)) return;
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO attachment_keys (mxc, file_json) VALUES (@m, @j)";
                cmd.Parameters.AddWithValue("@m", mxc);
                cmd.Parameters.AddWithValue("@j", fileJson);
                cmd.ExecuteNonQuery();
            }
        }

        public string GetAttachmentKey(string mxc)
        {
            if (string.IsNullOrEmpty(mxc)) return null;
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT file_json FROM attachment_keys WHERE mxc = @m";
                cmd.Parameters.AddWithValue("@m", mxc);
                return cmd.ExecuteScalar() as string;
            }
        }

        // ---- Olm account ----

        public string GetAccountPickle()
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT pickle FROM crypto_account WHERE id = 1";
                return cmd.ExecuteScalar() as string;
            }
        }

        public void SaveAccount(string pickle, string deviceId, string ed25519, string curve25519)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO crypto_account(id, pickle, device_id, ed25519, curve25519)
                    VALUES(1, @p, @d, @e, @c)";
                cmd.Parameters.AddWithValue("@p", (object)pickle ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@d", (object)deviceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@e", (object)ed25519 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@c", (object)curve25519 ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateAccountPickle(string pickle)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE crypto_account SET pickle = @p WHERE id = 1";
                cmd.Parameters.AddWithValue("@p", (object)pickle ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Olm sessions ----

        public List<StoredOlmSession> GetOlmSessions(string senderKey)
        {
            var list = new List<StoredOlmSession>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT session_id, pickle, last_used FROM olm_sessions
                                    WHERE sender_key = @k ORDER BY last_used DESC";
                cmd.Parameters.AddWithValue("@k", senderKey);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StoredOlmSession
                        {
                            SessionId = r.GetString(0),
                            Pickle = r.GetString(1),
                            LastUsed = r.IsDBNull(2) ? 0 : r.GetInt64(2)
                        });
                    }
                }
            }
            return list;
        }

        public void SaveOlmSession(string senderKey, string sessionId, string pickle, long lastUsed)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO olm_sessions(sender_key, session_id, pickle, last_used)
                    VALUES(@k, @s, @p, @t)";
                cmd.Parameters.AddWithValue("@k", senderKey);
                cmd.Parameters.AddWithValue("@s", sessionId);
                cmd.Parameters.AddWithValue("@p", pickle);
                cmd.Parameters.AddWithValue("@t", lastUsed);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Inbound Megolm sessions ----

        public StoredInboundGroupSession GetInboundGroupSession(string roomId, string senderKey, string sessionId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT pickle, ed25519, forwarded FROM megolm_in
                                    WHERE room_id = @r AND sender_key = @k AND session_id = @s LIMIT 1";
                cmd.Parameters.AddWithValue("@r", roomId);
                cmd.Parameters.AddWithValue("@k", senderKey);
                cmd.Parameters.AddWithValue("@s", sessionId);
                using (var rd = cmd.ExecuteReader())
                {
                    if (rd.Read())
                    {
                        return new StoredInboundGroupSession
                        {
                            RoomId = roomId,
                            SenderKey = senderKey,
                            SessionId = sessionId,
                            Pickle = rd.GetString(0),
                            Ed25519 = rd.IsDBNull(1) ? null : rd.GetString(1),
                            Forwarded = !rd.IsDBNull(2) && rd.GetInt64(2) != 0
                        };
                    }
                }
            }
            return null;
        }

        public void SaveInboundGroupSession(StoredInboundGroupSession s)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO megolm_in(room_id, sender_key, session_id, pickle, ed25519, forwarded)
                    VALUES(@r, @k, @s, @p, @e, @f)";
                cmd.Parameters.AddWithValue("@r", s.RoomId);
                cmd.Parameters.AddWithValue("@k", s.SenderKey);
                cmd.Parameters.AddWithValue("@s", s.SessionId);
                cmd.Parameters.AddWithValue("@p", s.Pickle);
                cmd.Parameters.AddWithValue("@e", (object)s.Ed25519 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@f", s.Forwarded ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        public bool HasInboundGroupSession(string roomId, string senderKey, string sessionId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT COUNT(*) FROM megolm_in
                                    WHERE room_id = @r AND sender_key = @k AND session_id = @s";
                cmd.Parameters.AddWithValue("@r", roomId);
                cmd.Parameters.AddWithValue("@k", senderKey);
                cmd.Parameters.AddWithValue("@s", sessionId);
                return (long)cmd.ExecuteScalar() > 0;
            }
        }

        public List<StoredInboundGroupSession> GetAllInboundGroupSessions()
        {
            var list = new List<StoredInboundGroupSession>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT room_id, sender_key, session_id, pickle, ed25519, forwarded FROM megolm_in";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StoredInboundGroupSession
                        {
                            RoomId = r.GetString(0),
                            SenderKey = r.GetString(1),
                            SessionId = r.GetString(2),
                            Pickle = r.GetString(3),
                            Ed25519 = r.IsDBNull(4) ? null : r.GetString(4),
                            Forwarded = !r.IsDBNull(5) && r.GetInt64(5) != 0
                        });
                    }
                }
            }
            return list;
        }

        // ---- Outbound Megolm sessions ----

        public StoredOutboundGroupSession GetOutboundGroupSession(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT pickle, session_id, created_ts, msg_count, shared_to FROM megolm_out
                                    WHERE room_id = @r LIMIT 1";
                cmd.Parameters.AddWithValue("@r", roomId);
                using (var rd = cmd.ExecuteReader())
                {
                    if (rd.Read())
                    {
                        return new StoredOutboundGroupSession
                        {
                            RoomId = roomId,
                            Pickle = rd.GetString(0),
                            SessionId = rd.IsDBNull(1) ? null : rd.GetString(1),
                            CreatedTs = rd.IsDBNull(2) ? 0 : rd.GetInt64(2),
                            MsgCount = rd.IsDBNull(3) ? 0 : (int)rd.GetInt64(3),
                            SharedTo = rd.IsDBNull(4) ? null : rd.GetString(4)
                        };
                    }
                }
            }
            return null;
        }

        public void SaveOutboundGroupSession(StoredOutboundGroupSession s)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO megolm_out(room_id, pickle, session_id, created_ts, msg_count, shared_to)
                    VALUES(@r, @p, @s, @c, @m, @sh)";
                cmd.Parameters.AddWithValue("@r", s.RoomId);
                cmd.Parameters.AddWithValue("@p", s.Pickle);
                cmd.Parameters.AddWithValue("@s", (object)s.SessionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@c", s.CreatedTs);
                cmd.Parameters.AddWithValue("@m", s.MsgCount);
                cmd.Parameters.AddWithValue("@sh", (object)s.SharedTo ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteOutboundGroupSession(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM megolm_out WHERE room_id = @r";
                cmd.Parameters.AddWithValue("@r", roomId);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Device keys ----

        public List<StoredDevice> GetDevices(string userId)
        {
            var list = new List<StoredDevice>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT device_id, curve25519, ed25519, json, trust FROM device_keys
                                    WHERE user_id = @u";
                cmd.Parameters.AddWithValue("@u", userId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new StoredDevice
                        {
                            UserId = userId,
                            DeviceId = r.GetString(0),
                            Curve25519 = r.IsDBNull(1) ? null : r.GetString(1),
                            Ed25519 = r.IsDBNull(2) ? null : r.GetString(2),
                            Json = r.IsDBNull(3) ? null : r.GetString(3),
                            Trust = r.IsDBNull(4) ? 0 : (int)r.GetInt64(4)
                        });
                    }
                }
            }
            return list;
        }

        public StoredDevice GetDevice(string userId, string deviceId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT curve25519, ed25519, json, trust FROM device_keys
                                    WHERE user_id = @u AND device_id = @d LIMIT 1";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@d", deviceId);
                using (var rd = cmd.ExecuteReader())
                {
                    if (rd.Read())
                    {
                        return new StoredDevice
                        {
                            UserId = userId,
                            DeviceId = deviceId,
                            Curve25519 = rd.IsDBNull(0) ? null : rd.GetString(0),
                            Ed25519 = rd.IsDBNull(1) ? null : rd.GetString(1),
                            Json = rd.IsDBNull(2) ? null : rd.GetString(2),
                            Trust = rd.IsDBNull(3) ? 0 : (int)rd.GetInt64(3)
                        };
                    }
                }
            }
            return null;
        }

        public void SaveDevice(StoredDevice d)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO device_keys(user_id, device_id, curve25519, ed25519, json, trust)
                    VALUES(@u, @d, @c, @e, @j, @t)";
                cmd.Parameters.AddWithValue("@u", d.UserId);
                cmd.Parameters.AddWithValue("@d", d.DeviceId);
                cmd.Parameters.AddWithValue("@c", (object)d.Curve25519 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@e", (object)d.Ed25519 ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@j", (object)d.Json ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@t", d.Trust);
                cmd.ExecuteNonQuery();
            }
        }

        public void DeleteDevicesForUser(string userId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM device_keys WHERE user_id = @u";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.ExecuteNonQuery();
            }
        }

        // ---- Tracked users (device-list tracking) ----

        public void SetTrackedUser(string userId, bool dirty)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "INSERT OR REPLACE INTO tracked_users(user_id, dirty) VALUES(@u, @d)";
                cmd.Parameters.AddWithValue("@u", userId);
                cmd.Parameters.AddWithValue("@d", dirty ? 1 : 0);
                cmd.ExecuteNonQuery();
            }
        }

        public bool IsUserTracked(string userId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM tracked_users WHERE user_id = @u";
                cmd.Parameters.AddWithValue("@u", userId);
                return (long)cmd.ExecuteScalar() > 0;
            }
        }

        public List<string> GetDirtyUsers()
        {
            var list = new List<string>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT user_id FROM tracked_users WHERE dirty = 1";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(r.GetString(0));
                }
            }
            return list;
        }

        // ---- Per-room encryption state ----

        public string GetRoomAlgorithm(string roomId)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT algorithm FROM e2ee_rooms WHERE room_id = @r LIMIT 1";
                cmd.Parameters.AddWithValue("@r", roomId);
                return cmd.ExecuteScalar() as string;
            }
        }

        public bool IsRoomEncrypted(string roomId)
        {
            return !string.IsNullOrEmpty(GetRoomAlgorithm(roomId));
        }

        public void SetRoomEncryption(string roomId, string algorithm, long rotationMs, int rotationMsgs)
        {
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"
                    INSERT OR REPLACE INTO e2ee_rooms(room_id, algorithm, rotation_ms, rotation_msgs)
                    VALUES(@r, @a, @ms, @n)";
                cmd.Parameters.AddWithValue("@r", roomId);
                cmd.Parameters.AddWithValue("@a", (object)algorithm ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ms", rotationMs);
                cmd.Parameters.AddWithValue("@n", rotationMsgs);
                cmd.ExecuteNonQuery();
            }
        }

        public List<string> GetEncryptedRoomIds()
        {
            var list = new List<string>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = "SELECT room_id FROM e2ee_rooms WHERE algorithm IS NOT NULL";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read()) list.Add(r.GetString(0));
                }
            }
            return list;
        }

        /// <summary>Returns the event ids + raw content of still-encrypted messages in a room
        /// (msgtype m.room.encrypted), so they can be retried once keys arrive.</summary>
        public List<KeyValuePair<string, string>> GetEncryptedMessages(string roomId)
        {
            var list = new List<KeyValuePair<string, string>>();
            lock (_gate)
            using (var cmd = _connection.CreateCommand())
            {
                cmd.CommandText = @"SELECT event_id, body FROM messages
                                    WHERE room_id = @r AND msgtype = 'm.room.encrypted'";
                cmd.Parameters.AddWithValue("@r", roomId);
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        list.Add(new KeyValuePair<string, string>(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1)));
                }
            }
            return list;
        }
    }
}
