using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using Newtonsoft.Json;

namespace RampageTracker.Core
{
    public sealed class ApiKeyUsageTracker
    {
        private sealed class UsageState
        {
            public string? Date { get; set; }
            public int Count { get; set; }
        }

        private readonly string _path;
        private readonly int _threshold;
        private readonly object _sync = new();
        private bool _loaded;
        private string _currentDate = string.Empty;
        private int _count;
        private int _lastPersistCount;
        private DateTime _lastPersistUtc = DateTime.MinValue;
        private readonly TimeSpan _persistInterval = TimeSpan.FromSeconds(10);
        private const int PersistEveryCount = 200;

        public ApiKeyUsageTracker(string path, int threshold)
        {
            _path = path;
            _threshold = Math.Max(0, threshold);
        }

        public bool ShouldUseKey(out bool activated)
        {
            var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            bool nowActive;
            bool activatedLocal;
            lock (_sync)
            {
                if (!_loaded)
                {
                    LoadState();
                }

                if (!string.Equals(_currentDate, today, StringComparison.Ordinal))
                {
                    _currentDate = today;
                    _count = 0;
                    _lastPersistCount = 0;
                    _lastPersistUtc = DateTime.UtcNow;
                    SaveState();
                }

                var wasActive = _count > _threshold;
                _count++;
                nowActive = _count > _threshold;
                activatedLocal = !wasActive && nowActive;

                var now = DateTime.UtcNow;
                if ((_count - _lastPersistCount) >= PersistEveryCount || (now - _lastPersistUtc) >= _persistInterval)
                {
                    SaveState();
                    _lastPersistCount = _count;
                    _lastPersistUtc = now;
                }
            }

            activated = activatedLocal;
            return nowActive;
        }

        private void LoadState()
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    var state = JsonConvert.DeserializeObject<UsageState>(json);
                    if (state != null)
                    {
                        _currentDate = state.Date ?? string.Empty;
                        _count = state.Count;
                    }
                }
            }
            catch
            {
                _currentDate = string.Empty;
                _count = 0;
            }
            _loaded = true;
            _lastPersistCount = _count;
            _lastPersistUtc = DateTime.UtcNow;
        }

        private void SaveState()
        {
            try
            {
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var state = new UsageState { Date = _currentDate, Count = _count };
                var json = JsonConvert.SerializeObject(state, Formatting.Indented);
                File.WriteAllText(_path, json);
            }
            catch
            {
                // Best effort; if write fails, usage tracking resets next run.
            }
        }
    }
}
