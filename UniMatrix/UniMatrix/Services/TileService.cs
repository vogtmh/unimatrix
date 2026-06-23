using System;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace UniMatrix.Services
{
    /// <summary>
    /// Updates the primary app tile so it works as a live tile on the Start screen, showing the
    /// latest message (room/sender name + text). Uses adaptive tile templates so the text shows on
    /// every tile size. A null/empty payload clears the tile back to its default logo.
    /// </summary>
    internal static class TileService
    {
        /// <summary>Shows the latest message on the live tile.</summary>
        public static void UpdateLatest(string roomName, string message)
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();

            if (string.IsNullOrWhiteSpace(roomName) && string.IsNullOrWhiteSpace(message))
            {
                updater.Clear();
                return;
            }

            try
            {
                updater.Update(new TileNotification(BuildTileXml(roomName, message)));
            }
            catch (Exception ex)
            {
                // A bad tile payload should never crash the app/background host.
                App.Log("Tile update failed: " + ex.Message);
            }
        }

        /// <summary>Resets the tile back to its default logo.</summary>
        public static void Clear()
        {
            try { TileUpdateManager.CreateTileUpdaterForApplication().Clear(); }
            catch (Exception ex) { App.Log("Tile clear failed: " + ex.Message); }
        }

        private static XmlDocument BuildTileXml(string roomName, string message)
        {
            string title = Escape(roomName ?? "");
            string body = Escape(message ?? "");

            string titleLine = string.IsNullOrEmpty(title)
                ? ""
                : "<text hint-style=\"captionSubtle\">" + title + "</text>";

            string payload =
                "<tile>" +
                  "<visual branding=\"name\">" +

                    "<binding template=\"TileMedium\" branding=\"none\">" +
                      "<text hint-style=\"caption\" hint-wrap=\"true\" hint-maxLines=\"1\">" + title + "</text>" +
                      "<text hint-style=\"captionSubtle\" hint-wrap=\"true\" hint-maxLines=\"4\">" + body + "</text>" +
                    "</binding>" +

                    "<binding template=\"TileWide\">" +
                      titleLine +
                      "<text hint-style=\"base\" hint-wrap=\"true\" hint-maxLines=\"3\">" + body + "</text>" +
                    "</binding>" +

                    "<binding template=\"TileLarge\">" +
                      titleLine +
                      "<text hint-style=\"subtitle\" hint-wrap=\"true\" hint-maxLines=\"6\">" + body + "</text>" +
                    "</binding>" +

                  "</visual>" +
                "</tile>";

            var doc = new XmlDocument();
            doc.LoadXml(payload);
            return doc;
        }

        private static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }
    }
}
