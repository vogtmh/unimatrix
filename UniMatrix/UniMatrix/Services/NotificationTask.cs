using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Data.Json;
using UniMatrix.Data;
using UniMatrix.Models;

namespace UniMatrix.Services
{
    /// <summary>
    /// In-process background task that periodically does a short incremental /sync and raises a
    /// toast for new incoming messages, respecting the per-type notification toggles (direct
    /// messages vs group rooms). Registered on launch, handled in App.OnBackgroundActivated.
    /// </summary>
    public static class NotificationTask
    {
        public const string TaskName = "UniMatrix.MessageNotify";

        // The OS enforces a 15-minute minimum interval for a TimeTrigger.
        private const uint IntervalMinutes = 15;

        /// <summary>Registers (or re-registers) the periodic notification task. Idempotent.</summary>
        public static async Task RegisterAsync()
        {
            try
            {
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                if (access == BackgroundAccessStatus.DeniedBySystemPolicy ||
                    access == BackgroundAccessStatus.DeniedByUser)
                {
                    return;
                }

                foreach (var existing in BackgroundTaskRegistration.AllTasks.Values)
                {
                    if (existing.Name == TaskName) existing.Unregister(true);
                }

                var builder = new BackgroundTaskBuilder { Name = TaskName };
                builder.SetTrigger(new TimeTrigger(IntervalMinutes, false));
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                // No TaskEntryPoint: this is an in-process task handled in App.OnBackgroundActivated.
                builder.Register();
            }
            catch (Exception ex)
            {
                App.Log("Notification task register failed: " + ex.Message);
            }
        }

        /// <summary>Removes the periodic notification task (e.g. on sign-out).</summary>
        public static void Unregister()
        {
            try
            {
                foreach (var existing in BackgroundTaskRegistration.AllTasks.Values)
                {
                    if (existing.Name == TaskName) existing.Unregister(true);
                }
            }
            catch (Exception ex)
            {
                App.Log("Notification task unregister failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Runs one notification pass: a short incremental sync, then a toast for each room with new
        /// incoming messages that matches the user's notification preferences, and a live-tile
        /// update with the latest message that matches the tile preference. Called from
        /// App.OnBackgroundActivated. Never throws (best effort).
        /// </summary>
        public static async Task RunAsync()
        {
            var settings = new PreferencesService();

            bool toastsEnabled = settings.NotifyDirectMessages || settings.NotifyGroupRooms;
            bool tileEnabled = settings.LiveTileMode != LiveTileMode.Off;

            // Nothing to do if neither toasts nor the live tile are on, or we're signed out.
            if (!toastsEnabled && !tileEnabled) return;
            string token = settings.GetAccessToken();
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(settings.UserId)) return;

            string dbPath = System.IO.Path.Combine(
                Windows.Storage.ApplicationData.Current.LocalFolder.Path, "unimatrix.db");

            MatrixDatabase db = null;
            try
            {
                db = new MatrixDatabase(dbPath);
                await db.OpenAsync();
                db.CreateSchema();

                // Only run as an incremental sync from an existing token. If there is none, the app
                // has never completed a full sync; doing that heavy work in the background is wrong,
                // so we skip and let the foreground app handle the first sync.
                string since = db.GetMeta("next_batch");
                if (string.IsNullOrEmpty(since)) return;

                var client = new MatrixClient();
                client.SetHomeserver(settings.Homeserver);
                client.AccessToken = token;
                client.UserId = settings.UserId;

                var processor = new SyncProcessor(db, settings.UserId);

                // timeout 0 = return immediately with whatever has accumulated since the token.
                JsonObject resp = await client.SyncAsync(since, 0, CancellationToken.None);
                var result = processor.Process(resp);
                if (!string.IsNullOrEmpty(result.NextBatch))
                    db.SetMeta("next_batch", result.NextBatch);

                // The newest tile-eligible message across all changed rooms (most recent wins).
                Room tileRoom = null;
                Message tileMessage = null;

                foreach (var roomId in result.ChangedRooms)
                {
                    try
                    {
                        Room room = db.GetRoom(roomId);
                        if (room == null || room.UnreadCount <= 0) continue;

                        Message latest = db.GetLatestIncomingMessage(roomId, settings.UserId);
                        if (latest == null) continue;

                        if (toastsEnabled)
                            NotifyRoomIfNeeded(db, settings, room, latest);

                        if (tileEnabled && TileWants(settings.LiveTileMode, room.IsDirect) &&
                            (tileMessage == null || latest.Timestamp > tileMessage.Timestamp))
                        {
                            tileMessage = latest;
                            tileRoom = room;
                        }
                    }
                    catch (Exception ex) { App.Log("Notify room error (" + roomId + "): " + ex.Message); }
                }

                if (tileEnabled && tileMessage != null)
                    TileService.UpdateLatest(tileRoom.DisplayName, Preview(tileMessage));
            }
            catch (Exception ex)
            {
                App.Log("Notification run failed: " + ex.Message);
            }
            finally
            {
                try { db?.Dispose(); } catch { }
            }
        }

        /// <summary>Whether a room of the given direct/group kind matches the live-tile mode.</summary>
        private static bool TileWants(LiveTileMode mode, bool isDirect)
        {
            switch (mode)
            {
                case LiveTileMode.All: return true;
                case LiveTileMode.DirectOnly: return isDirect;
                case LiveTileMode.GroupsOnly: return !isDirect;
                default: return false; // Off
            }
        }

        private static void NotifyRoomIfNeeded(MatrixDatabase db, PreferencesService settings, Room room, Message latest)
        {
            // Respect the per-type toggles.
            if (room.IsDirect && !settings.NotifyDirectMessages) return;
            if (!room.IsDirect && !settings.NotifyGroupRooms) return;

            // Don't re-notify for a message we've already toasted (the background task runs every
            // 15 min and a room can stay unread across several passes).
            string lastNotified = db.GetMeta("notif_" + room.Id);
            if (lastNotified == latest.EventId) return;

            ToastService.ShowMessage(room.Id, room.DisplayName, Preview(latest));
            db.SetMeta("notif_" + room.Id, latest.EventId);
        }

        private static string Preview(Message m)
        {
            if (m == null) return "";
            if (m.MsgType == "m.image") return "\uD83D\uDCF7 Photo";
            return m.Body ?? "";
        }
    }
}
