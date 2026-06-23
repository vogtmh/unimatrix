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
        /// incoming messages that matches the user's notification preferences. Called from
        /// App.OnBackgroundActivated. Never throws (best effort).
        /// </summary>
        public static async Task RunAsync()
        {
            var settings = new PreferencesService();

            // Nothing to do if both notification types are off or we're signed out.
            if (!settings.NotifyDirectMessages && !settings.NotifyGroupRooms) return;
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

                foreach (var roomId in result.ChangedRooms)
                {
                    try { NotifyRoomIfNeeded(db, settings, roomId); }
                    catch (Exception ex) { App.Log("Notify room error (" + roomId + "): " + ex.Message); }
                }
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

        private static void NotifyRoomIfNeeded(MatrixDatabase db, PreferencesService settings, string roomId)
        {
            Room room = db.GetRoom(roomId);
            if (room == null || room.UnreadCount <= 0) return;

            // Respect the per-type toggles.
            if (room.IsDirect && !settings.NotifyDirectMessages) return;
            if (!room.IsDirect && !settings.NotifyGroupRooms) return;

            Message latest = db.GetLatestIncomingMessage(roomId, settings.UserId);
            if (latest == null) return;

            // Don't re-notify for a message we've already toasted (the background task runs every
            // 15 min and a room can stay unread across several passes).
            string lastNotified = db.GetMeta("notif_" + roomId);
            if (lastNotified == latest.EventId) return;

            ToastService.ShowMessage(roomId, room.DisplayName, Preview(latest));
            db.SetMeta("notif_" + roomId, latest.EventId);
        }

        private static string Preview(Message m)
        {
            if (m == null) return "";
            if (m.MsgType == "m.image") return "\uD83D\uDCF7 Photo";
            return m.Body ?? "";
        }
    }
}
