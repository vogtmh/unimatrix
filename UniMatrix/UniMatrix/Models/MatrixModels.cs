using System;
using System.Collections.Generic;
using System.ComponentModel;
using Windows.UI.Xaml;

namespace UniMatrix.Models
{
    /// <summary>
    /// A Matrix room as shown in the room list and used as the chat header.
    /// </summary>
    public class Room : INotifyPropertyChanged
    {
        public string Id { get; set; }

        private string _name;
        public string Name
        {
            get { return _name; }
            set { _name = value; Raise("Name"); Raise("DisplayName"); Raise("AvatarInitial"); }
        }

        public string Topic { get; set; }
        public string AvatarMxc { get; set; }

        /// <summary>True when this room is a direct (1:1) message, per the m.direct account data.
        /// Drives the per-type notification toggles (DMs vs group rooms).</summary>
        public bool IsDirect { get; set; }

        private bool _isInvite;
        /// <summary>True when the user has been invited to this room but hasn't joined yet. Such
        /// rooms appear in the list with an "Invitation" hint and a tap offers Accept/Decline
        /// instead of opening the (not-yet-joined) timeline.</summary>
        public bool IsInvite
        {
            get { return _isInvite; }
            set { _isInvite = value; Raise("IsInvite"); Raise("InviteVisibility"); Raise("PreviewVisibility"); }
        }

        /// <summary>For an invited room, the @user:server who sent the invite (shown on the invite
        /// screen). Null for joined rooms.</summary>
        public string Inviter { get; set; }

        private string _avatarUrl;
        /// <summary>Resolved https:// URL for the room avatar, or null when unset.</summary>
        public string AvatarUrl
        {
            get { return _avatarUrl; }
            set { _avatarUrl = value; Raise("AvatarUrl"); Raise("HasAvatar"); Raise("AvatarVisibility"); Raise("InitialVisibility"); }
        }

        private int _memberCount;
        public int MemberCount
        {
            get { return _memberCount; }
            set { _memberCount = value; Raise("MemberCount"); Raise("MemberText"); }
        }

        private int _unread;
        public int UnreadCount
        {
            get { return _unread; }
            set { _unread = value; Raise("UnreadCount"); Raise("HasUnread"); Raise("UnreadVisibility"); Raise("UnreadText"); }
        }

        public long LastEventTs { get; set; }

        private string _lastPreview;
        public string LastPreview
        {
            get { return _lastPreview; }
            set { _lastPreview = value; Raise("LastPreview"); }
        }

        private bool _isEncrypted;
        /// <summary>True once the room has m.room.encryption enabled. Drives the lock indicator.</summary>
        public bool IsEncrypted
        {
            get { return _isEncrypted; }
            set { _isEncrypted = value; Raise("IsEncrypted"); Raise("EncryptedVisibility"); }
        }

        /// <summary>Visibility of the lock glyph in the room list / header.</summary>
        public Visibility EncryptedVisibility
        {
            get { return _isEncrypted ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string DisplayName
        {
            get { return string.IsNullOrEmpty(Name) ? Id : Name; }
        }

        /// <summary>First letter used for the generated fallback avatar.</summary>
        public string AvatarInitial
        {
            get
            {
                string n = DisplayName;
                if (string.IsNullOrEmpty(n)) return "?";
                foreach (char c in n)
                {
                    if (char.IsLetterOrDigit(c)) return char.ToUpper(c).ToString();
                }
                return "#";
            }
        }

        public bool HasAvatar { get { return !string.IsNullOrEmpty(_avatarUrl); } }
        public Visibility AvatarVisibility { get { return HasAvatar ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility InitialVisibility { get { return HasAvatar ? Visibility.Collapsed : Visibility.Visible; } }

        public bool HasUnread { get { return _unread > 0; } }
        public Visibility UnreadVisibility { get { return _unread > 0 ? Visibility.Visible : Visibility.Collapsed; } }
        public string UnreadText { get { return _unread > 99 ? "99+" : _unread.ToString(); } }

        /// <summary>Shows the "Invitation" hint line for a pending invite.</summary>
        public Visibility InviteVisibility { get { return _isInvite ? Visibility.Visible : Visibility.Collapsed; } }
        /// <summary>Hides the normal last-message preview while the room is just an invitation.</summary>
        public Visibility PreviewVisibility { get { return _isInvite ? Visibility.Collapsed : Visibility.Visible; } }

        public string MemberText
        {
            get { return _memberCount <= 0 ? "" : (_memberCount == 1 ? "1 member" : _memberCount + " members"); }
        }

        /// <summary>Deterministic accent color for the fallback avatar, derived from the room id.</summary>
        public Windows.UI.Color AvatarColor
        {
            get
            {
                int hash = (Id ?? DisplayName ?? "?").GetHashCode();
                byte r = (byte)(60 + (Math.Abs(hash) % 150));
                byte g = (byte)(60 + (Math.Abs(hash / 7) % 150));
                byte b = (byte)(60 + (Math.Abs(hash / 13) % 150));
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
        }

        public Windows.UI.Xaml.Media.SolidColorBrush AvatarBrush
        {
            get { return new Windows.UI.Xaml.Media.SolidColorBrush(AvatarColor); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// A public room returned by the homeserver's room directory (publicRooms).
    /// Used only for the "Add room" browse list; not persisted.
    /// </summary>
    public class PublicRoomEntry
    {
        public string RoomId { get; set; }
        public string Name { get; set; }
        public string Topic { get; set; }
        public string CanonicalAlias { get; set; }
        public int MemberCount { get; set; }

        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (!string.IsNullOrEmpty(CanonicalAlias)) return CanonicalAlias;
                return RoomId;
            }
        }

        /// <summary>Prefer the alias when joining — it is more federation-friendly than a bare room id.</summary>
        public string JoinTarget
        {
            get { return !string.IsNullOrEmpty(CanonicalAlias) ? CanonicalAlias : RoomId; }
        }

        /// <summary>Second line: topic when present, otherwise the address.</summary>
        public string TopicLine
        {
            get { return string.IsNullOrEmpty(Topic) ? (CanonicalAlias ?? RoomId) : Topic.Replace("\n", " ").Replace("\r", " "); }
        }

        public string MembersText
        {
            get { return MemberCount == 1 ? "1 member" : MemberCount + " members"; }
        }

        public string AvatarInitial
        {
            get
            {
                string n = DisplayName;
                if (string.IsNullOrEmpty(n)) return "?";
                foreach (char c in n)
                {
                    if (char.IsLetterOrDigit(c)) return char.ToUpper(c).ToString();
                }
                return "#";
            }
        }

        public Windows.UI.Color AvatarColor
        {
            get
            {
                int hash = (RoomId ?? DisplayName ?? "?").GetHashCode();
                byte r = (byte)(60 + (Math.Abs(hash) % 150));
                byte g = (byte)(60 + (Math.Abs(hash / 7) % 150));
                byte b = (byte)(60 + (Math.Abs(hash / 13) % 150));
                return Windows.UI.Color.FromArgb(255, r, g, b);
            }
        }

        public Windows.UI.Xaml.Media.SolidColorBrush AvatarBrush
        {
            get { return new Windows.UI.Xaml.Media.SolidColorBrush(AvatarColor); }
        }
    }

    /// <summary>
    /// TURN/STUN credentials returned by the homeserver's /voip/turnServer endpoint, used to build
    /// the ICE server list for WebRTC calls.
    /// </summary>
    public class TurnServerInfo
    {
        public List<string> Uris { get; } = new List<string>();
        public string Username { get; set; }
        public string Password { get; set; }
    }

    /// <summary>
    /// A single message event displayed inside a room's timeline.
    /// </summary>
    public class Message : INotifyPropertyChanged
    {        public string EventId { get; set; }
        public string RoomId { get; set; }
        public string Sender { get; set; }

        /// <summary>Matrix msgtype, e.g. m.text, m.notice, m.image.</summary>
        public string MsgType { get; set; }

        private string _body;
        public string Body
        {
            get { return _body; }
            set { _body = value; Raise("Body"); }
        }

        public long Timestamp { get; set; }

        /// <summary>Original mxc:// URL for media messages (m.image), if any.</summary>
        public string Mxc { get; set; }

        private string _mediaUrl;
        /// <summary>Resolved https:// URL for downloading/displaying media.</summary>
        public string MediaUrl
        {
            get { return _mediaUrl; }
            set { _mediaUrl = value; Raise("MediaUrl"); Raise("HasImage"); Raise("ImageVisibility"); Raise("TextVisibility"); }
        }

        private bool _isMine;
        public bool IsMine
        {
            get { return _isMine; }
            set { _isMine = value; Raise("IsMine"); Raise("Alignment"); Raise("BubbleBrush"); Raise("SenderVisibility"); }
        }

        /// <summary>True while a message sent locally awaits confirmation from /sync.</summary>
        public bool IsLocalEcho { get; set; }

        public string SenderDisplay { get; set; }

        public bool IsImage { get { return MsgType == "m.image"; } }
        public bool HasImage { get { return IsImage && !string.IsNullOrEmpty(_mediaUrl); } }
        public Visibility ImageVisibility { get { return HasImage ? Visibility.Visible : Visibility.Collapsed; } }
        public Visibility TextVisibility { get { return (HasImage || IsCall) ? Visibility.Collapsed : Visibility.Visible; } }

        // ---- Location (m.location) rendering ----
        // The point is carried in the (otherwise unused) Mxc field as the event's geo_uri
        // ("geo:lat,lon" with an optional ";u=accuracy" suffix). The inline preview is a single
        // static OpenStreetMap tile with a pin overlay; tapping it opens the interactive
        // fullscreen Leaflet map. Coordinates are parsed once and cached.
        private const int MapPreviewZoom = 15;
        private const double MapPreviewSize = 240;  // inline preview edge length, in DIPs
        private bool _geoParsed;
        private bool _geoValid;
        private double _lat, _lon;

        private void ParseGeo()
        {
            if (_geoParsed) return;
            _geoParsed = true;
            _geoValid = false;
            if (MsgType != "m.location" || string.IsNullOrEmpty(Mxc)) return;
            try
            {
                string s = Mxc.Trim();
                if (s.StartsWith("geo:", StringComparison.OrdinalIgnoreCase)) s = s.Substring(4);
                int semi = s.IndexOf(';'); if (semi >= 0) s = s.Substring(0, semi);
                var parts = s.Split(',');
                if (parts.Length < 2) return;
                double la, lo;
                if (double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out la) &&
                    double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lo) &&
                    la >= -90 && la <= 90 && lo >= -180 && lo <= 180)
                {
                    _lat = la; _lon = lo; _geoValid = true;
                }
            }
            catch { _geoValid = false; }
        }

        public double Latitude { get { ParseGeo(); return _lat; } }
        public double Longitude { get { ParseGeo(); return _lon; } }

        /// <summary>True for a renderable location event carrying valid coordinates.</summary>
        public bool IsLocation { get { ParseGeo(); return MsgType == "m.location" && _geoValid; } }
        public Visibility LocationVisibility { get { return IsLocation ? Visibility.Visible : Visibility.Collapsed; } }

        // Web-Mercator projection of the point at the preview zoom, expressed in tile units.
        private double MercX { get { ParseGeo(); return (_lon + 180.0) / 360.0 * (1 << MapPreviewZoom); } }
        private double MercY
        {
            get
            {
                ParseGeo();
                double latRad = _lat * Math.PI / 180.0;
                return (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * (1 << MapPreviewZoom);
            }
        }

        /// <summary>Static OpenStreetMap tile URL for the inline preview (the tile that contains the point).</summary>
        public string MapPreviewUrl
        {
            get
            {
                if (!IsLocation) return null;
                int tileX = (int)Math.Floor(MercX);
                int tileY = (int)Math.Floor(MercY);
                return string.Format("https://a.tile.geofabrik.de/2b232a218fc74caab0859632066bb003/{0}/{1}/{2}.png", MapPreviewZoom, tileX, tileY);
            }
        }

        /// <summary>Margin that places the pin glyph's tip over the exact point within the preview tile.</summary>
        public Thickness MapPinMargin
        {
            get
            {
                if (!IsLocation) return new Thickness(0);
                double fx = MercX - Math.Floor(MercX);
                double fy = MercY - Math.Floor(MercY);
                double px = fx * MapPreviewSize;
                double py = fy * MapPreviewSize;
                // The pin glyph (FontSize 28) has its tip at bottom-centre, so shift left ~half a
                // glyph width and up ~one glyph height to anchor the tip on the point.
                return new Thickness(px - 14, py - 26, 0, 0);
            }
        }

        /// <summary>
        /// True for synthesized voice/video call events (m.call.*). These render as a
        /// centered system pill rather than a chat bubble.
        /// </summary>
        public bool IsCall { get { return MsgType == "m.call"; } }
        public Visibility CallVisibility { get { return IsCall ? Visibility.Visible : Visibility.Collapsed; } }

        // ---- Call summary (m.call) rendering ----
        // A single timeline row summarises an entire call. CallKind is the outcome category
        // (set by SyncProcessor from the correlated m.call.invite/answer/hangup/reject events):
        //   "outgoing"          - a call we placed that connected (green, shows duration)
        //   "incoming"          - a call we received and answered (blue, shows duration)
        //   "missed"            - a call we received but didn't answer (red)
        //   "outgoing_noanswer" - a call we placed that was never answered (green, "No answer")
        // CallSeconds is the connected duration (0 when not answered). CallAnswerTs is the
        // answer event timestamp, kept so a later hangup can compute the duration.
        private string _callKind;
        public string CallKind
        {
            get { return _callKind; }
            set
            {
                _callKind = value;
                Raise("CallKind"); Raise("CallIcon"); Raise("CallIconBrush"); Raise("CallLabel");
                Raise("CallDurationText"); Raise("CallDurationVisibility");
            }
        }

        private int _callSeconds;
        public int CallSeconds
        {
            get { return _callSeconds; }
            set { _callSeconds = value; Raise("CallSeconds"); Raise("CallDurationText"); Raise("CallDurationVisibility"); }
        }

        /// <summary>Timestamp (ms) of the m.call.answer, used to compute the duration on hangup.</summary>
        public long CallAnswerTs { get; set; }

        /// <summary>Segoe MDL2 glyph for the call row (a phone; colour conveys the outcome).</summary>
        public string CallIcon { get { return "\uE717"; } }

        /// <summary>Colour of the call icon: red (missed), blue (incoming), green (outgoing).</summary>
        public Windows.UI.Xaml.Media.Brush CallIconBrush
        {
            get
            {
                string hex;
                switch (_callKind)
                {
                    case "missed": hex = "#FF6B6B"; break;            // red
                    case "incoming": hex = "#4C9DFF"; break;          // blue
                    case "outgoing":
                    case "outgoing_noanswer": hex = "#4CD964"; break; // green
                    default: hex = "#BBBBBB"; break;                  // neutral (ringing/unknown)
                }
                return new Windows.UI.Xaml.Media.SolidColorBrush(HexColor(hex));
            }
        }

        /// <summary>Friendly label for the call row.</summary>
        public string CallLabel
        {
            get
            {
                switch (_callKind)
                {
                    case "missed": return "Missed call";
                    case "incoming": return "Incoming call";
                    case "outgoing":
                    case "outgoing_noanswer": return "Outgoing call";
                    default: return "Call";
                }
            }
        }

        /// <summary>Duration ("M:SS"/"H:MM:SS") for answered calls, or "No answer" for an unanswered outgoing call.</summary>
        public string CallDurationText
        {
            get
            {
                if (_callSeconds > 0)
                {
                    var ts = TimeSpan.FromSeconds(_callSeconds);
                    return ts.Hours > 0
                        ? string.Format("{0}:{1:D2}:{2:D2}", (int)ts.TotalHours, ts.Minutes, ts.Seconds)
                        : string.Format("{0}:{1:D2}", ts.Minutes, ts.Seconds);
                }
                if (_callKind == "outgoing_noanswer") return "No answer";
                return "";
            }
        }

        public Visibility CallDurationVisibility
        {
            get { return string.IsNullOrEmpty(CallDurationText) ? Visibility.Collapsed : Visibility.Visible; }
        }

        /// <summary>Copies the evolving call state from a freshly-loaded row into this on-screen one.</summary>
        public void UpdateCallFrom(Message other)
        {
            if (other == null) return;
            CallAnswerTs = other.CallAnswerTs;
            CallSeconds = other.CallSeconds;
            CallKind = other.CallKind;
            Body = other.Body;
        }

        private static Windows.UI.Color HexColor(string hex)
        {
            hex = hex.TrimStart('#');
            byte r = System.Convert.ToByte(hex.Substring(0, 2), 16);
            byte g = System.Convert.ToByte(hex.Substring(2, 2), 16);
            byte b = System.Convert.ToByte(hex.Substring(4, 2), 16);
            return Windows.UI.Color.FromArgb(255, r, g, b);
        }

        /// <summary>The normal chat bubble is hidden for call events.</summary>
        public Visibility BubbleVisibility { get { return IsCall ? Visibility.Collapsed : Visibility.Visible; } }

        public HorizontalAlignment Alignment
        {
            get { return _isMine ? HorizontalAlignment.Right : HorizontalAlignment.Left; }
        }

        public Windows.UI.Xaml.Media.Brush BubbleBrush
        {
            get
            {
                var key = _isMine ? "AppOwnBubbleBrush" : "AppOtherBubbleBrush";
                object brush;
                if (Application.Current.Resources.TryGetValue(key, out brush))
                    return brush as Windows.UI.Xaml.Media.Brush;
                return new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Gray);
            }
        }

        /// <summary>Hide the sender label on your own messages (chat-bubble convention).</summary>
        public Visibility SenderVisibility
        {
            get { return _isMine ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string SenderShort
        {
            get
            {
                if (!string.IsNullOrEmpty(SenderDisplay)) return SenderDisplay;
                if (string.IsNullOrEmpty(Sender)) return "";
                // @user:server -> user
                string s = Sender;
                if (s.StartsWith("@")) s = s.Substring(1);
                int colon = s.IndexOf(':');
                if (colon > 0) s = s.Substring(0, colon);
                return s;
            }
        }

        public string TimeText
        {
            get
            {
                var dto = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime;
                return dto.ToString("HH:mm");
            }
        }

        /// <summary>
        /// When true, a date separator (Telegram-style pill) is shown above this message.
        /// Set during rendering whenever a message starts a new calendar day.
        /// </summary>
        private bool _showDateSeparator;
        public bool ShowDateSeparator
        {
            get { return _showDateSeparator; }
            set { _showDateSeparator = value; Raise("ShowDateSeparator"); Raise("DateSeparatorVisibility"); }
        }

        public Visibility DateSeparatorVisibility
        {
            get { return _showDateSeparator ? Visibility.Visible : Visibility.Collapsed; }
        }

        /// <summary>Human-friendly day label for the separator (Today / Yesterday / date).</summary>
        public string DateSeparatorText
        {
            get
            {
                var date = DateTimeOffset.FromUnixTimeMilliseconds(Timestamp).LocalDateTime.Date;
                var today = DateTime.Now.Date;
                if (date == today) return "Today";
                if (date == today.AddDays(-1)) return "Yesterday";
                if (date.Year == today.Year) return date.ToString("dddd, d MMMM");
                return date.ToString("d MMMM yyyy");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// A member of a room. Cached so sender names can be resolved without extra requests.
    /// </summary>
    public class Member
    {
        public string RoomId { get; set; }
        public string UserId { get; set; }
        public string DisplayName { get; set; }
        public string AvatarMxc { get; set; }
    }
}
