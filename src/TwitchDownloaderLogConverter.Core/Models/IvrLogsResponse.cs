using System.Diagnostics;

namespace TwitchDownloaderLogConverter.Core.Models
{
    /// <summary>
    /// Response of a logs.ivr.fi (rustlog) JSON logs request, e.g. <c>/channelid/{id}?from=..&amp;to=..&amp;json=1</c>.
    /// </summary>
    public class IvrLogsResponse
    {
        public List<IvrLogMessage> messages { get; set; } = new();
    }

    /// <summary>
    /// rustlog message types. These match the <c>type</c> field of a logs.ivr.fi JSON message.
    /// </summary>
    public enum IvrLogMessageType
    {
        Unknown = 0,
        PrivMsg = 1,
        ClearChat = 2,
        RoomState = 3,
        UserNotice = 4,
        UserState = 5,
        Notice = 6,
        ClearMsg = 13
    }

    [DebuggerDisplay("{displayName}: {text}")]
    public class IvrLogMessage
    {
        public string text { get; set; }
        public string displayName { get; set; }
        public DateTime timestamp { get; set; }
        public string id { get; set; }
        public Dictionary<string, string> tags { get; set; }
        public string username { get; set; }
        public string channel { get; set; }
        public string raw { get; set; }
        public int type { get; set; }
    }
}
