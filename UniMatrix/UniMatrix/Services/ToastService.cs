using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace UniMatrix.Services
{
    /// <summary>
    /// Shows local toast notifications for incoming messages. Each toast carries a
    /// launch argument ("room=&lt;roomId&gt;") so tapping it opens the relevant room.
    /// </summary>
    internal static class ToastService
    {
        /// <summary>
        /// Pops a toast with the room/sender name as the title and the message text as the body.
        /// The <paramref name="roomId"/> is encoded in the launch argument so the app can open that
        /// room when the user taps the notification.
        /// </summary>
        public static void ShowMessage(string roomId, string title, string body)
        {
            try
            {
                if (string.IsNullOrEmpty(title)) title = "New message";
                if (body == null) body = "";

                string launch = "room=" + (roomId ?? "");

                var xml = new XmlDocument();
                xml.LoadXml(
                    "<toast launch=\"" + Escape(launch) + "\">" +
                        "<visual>" +
                            "<binding template=\"ToastGeneric\">" +
                                "<text>" + Escape(title) + "</text>" +
                                "<text>" + Escape(body) + "</text>" +
                            "</binding>" +
                        "</visual>" +
                    "</toast>");

                var toast = new ToastNotification(xml)
                {
                    // Group/Tag by room so repeated notifications for the same room replace the
                    // previous one instead of stacking up.
                    Tag = SafeTag(roomId),
                    Group = "unimatrix"
                };

                ToastNotificationManager.CreateToastNotifier().Show(toast);
            }
            catch (Exception ex)
            {
                App.Log("Toast show failed: " + ex.Message);
            }
        }

        // Tags are limited to 64 chars; a Matrix room id can exceed that, so hash long ids.
        private static string SafeTag(string roomId)
        {
            if (string.IsNullOrEmpty(roomId)) return "room";
            if (roomId.Length <= 64) return roomId;
            return "r" + (uint)roomId.GetHashCode();
        }

        private static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;")
                    .Replace("'", "&apos;");
        }
    }
}
