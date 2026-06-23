using System;
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

        /// <summary>
        /// True for synthesized voice/video call events (m.call.*). These render as a
        /// centered system pill rather than a chat bubble.
        /// </summary>
        public bool IsCall { get { return MsgType == "m.call"; } }
        public Visibility CallVisibility { get { return IsCall ? Visibility.Visible : Visibility.Collapsed; } }

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
