using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using TwitchDownloaderCore.Interfaces;

namespace TwitchDownloaderLogConverter
{
    /// <summary>
    /// Forwards Core progress reports to the UI thread.
    /// </summary>
    internal sealed class UiTaskProgress : ITaskProgress
    {
        private readonly Action<int> _handlePercent;
        private readonly Action<string> _handleStatus;
        private readonly Action<string> _handleLog;
        private readonly bool _verbose;
        private readonly Lock _lock = new();

        private string _status = "";
        private bool _statusIsTemplate;
        private int _lastPercent = -1;

        public UiTaskProgress(Action<int> handlePercent, Action<string> handleStatus, Action<string> handleLog, bool verbose)
        {
            _handlePercent = handlePercent;
            _handleStatus = handleStatus;
            _handleLog = handleLog;
            _verbose = verbose;
        }

        private static void Post(Action action) => Dispatcher.UIThread.Post(action);

        public void SetStatus(string status)
        {
            lock (_lock)
            {
                _status = status;
                _statusIsTemplate = false;
                Post(() => _handleStatus(status));
            }
        }

        public void SetTemplateStatus([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string statusTemplate, int initialPercent)
        {
            lock (_lock)
            {
                _status = statusTemplate;
                _statusIsTemplate = true;
                _lastPercent = -1;
                ReportProgress(initialPercent);
            }
        }

        public void SetTemplateStatus([StringSyntax(StringSyntaxAttribute.CompositeFormat)] string statusTemplate, int initialPercent, TimeSpan initialTime1, TimeSpan initialTime2)
        {
            lock (_lock)
            {
                _status = statusTemplate;
                _statusIsTemplate = true;
                _lastPercent = -1;
                ReportProgress(initialPercent, initialTime1, initialTime2);
            }
        }

        public void ReportProgress(int percent)
        {
            lock (_lock)
            {
                if (_lastPercent == percent)
                    return;

                _lastPercent = percent;
                var status = _statusIsTemplate ? string.Format(_status, percent) : null;
                Post(() =>
                {
                    _handlePercent(percent);
                    if (status is not null)
                        _handleStatus(status);
                });
            }
        }

        public void ReportProgress(int percent, TimeSpan time1, TimeSpan time2)
        {
            lock (_lock)
            {
                _lastPercent = percent;
                var status = _statusIsTemplate ? string.Format(_status, percent, time1, time2) : null;
                Post(() =>
                {
                    _handlePercent(percent);
                    if (status is not null)
                        _handleStatus(status);
                });
            }
        }

        private void Log(string prefix, string message) => Post(() => _handleLog($"{prefix}{message}"));

        public void LogVerbose(string logMessage)
        {
            if (_verbose) Log("[verbose] ", logMessage);
        }

        public void LogVerbose(DefaultInterpolatedStringHandler logMessage)
        {
            var message = logMessage.ToStringAndClear();
            if (_verbose) Log("[verbose] ", message);
        }

        public void LogInfo(string logMessage) => Log("", logMessage);
        public void LogInfo(DefaultInterpolatedStringHandler logMessage) => Log("", logMessage.ToStringAndClear());
        public void LogWarning(string logMessage) => Log("[warning] ", logMessage);
        public void LogWarning(DefaultInterpolatedStringHandler logMessage) => Log("[warning] ", logMessage.ToStringAndClear());
        public void LogError(string logMessage) => Log("[error] ", logMessage);
        public void LogError(DefaultInterpolatedStringHandler logMessage) => Log("[error] ", logMessage.ToStringAndClear());
        public void LogFfmpeg(string logMessage) { }
    }
}
