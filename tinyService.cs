using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace AppZone
{
    public partial class AppZone : ServiceBase
    {
        private static readonly TimeSpan TimerInterval = TimeSpan.FromMinutes(1);
        private static Timer timer;
        private static int timerRunning = 0;
        private static volatile bool stopping = false;
        private const string EventSourceName = "AppZone";
        private const string EventLogName = "Application";

        private static readonly string[] MonitoredApplications =
        {
            "steam.exe",
            "dota.exe",
            "dota2.exe",
            "steam",
            "dota",
            "dota2"
        };

        private static readonly string[] MonitoredProcessNames = MonitoredApplications
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private const bool FailClosedWhenTimeUnavailable = true;

        public AppZone()
        {
            CanHandlePowerEvent = true;
            InitializeComponent();
        }

        protected override void OnStart(string[] args)
        {
            stopping = false;
            OnlineTimeProvider.ForceResync();
            timer = new Timer(OnTimerElapsed, null, TimerInterval, Timeout.InfiniteTimeSpan);
            ScheduleImmediateTick();
            _ = OnlineTimeProvider.GetUtcNowAsync();
        }

        protected override void OnStop()
        {
            stopping = true;
            try
            {
                timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown race.
            }
            timer?.Dispose();
        }

        protected override bool OnPowerEvent(PowerBroadcastStatus powerStatus)
        {
            switch (powerStatus)
            {
                case PowerBroadcastStatus.ResumeAutomatic:
                case PowerBroadcastStatus.ResumeCritical:
                case PowerBroadcastStatus.ResumeSuspend:
                    OnlineTimeProvider.ForceResync();
                    ScheduleImmediateTick();
                    _ = OnlineTimeProvider.GetUtcNowAsync();
                    break;
            }

            return base.OnPowerEvent(powerStatus);
        }

        private async void OnTimerElapsed(object state)
        {
            if (Volatile.Read(ref stopping))
            {
                return;
            }

            if (Interlocked.Exchange(ref timerRunning, 1) == 1)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref stopping))
                {
                    return;
                }

                await EnforcePolicyAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError("Unhandled error in timer callback.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref timerRunning, 0);
                if (!Volatile.Read(ref stopping))
                {
                    try
                    {
                        timer?.Change(TimerInterval, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Ignore shutdown race.
                    }
                }
            }
        }

        private static async Task EnforcePolicyAsync()
        {
            bool shouldBlock = false;

            DateTimeOffset? utcNow = await OnlineTimeProvider.GetUtcNowAsync().ConfigureAwait(false);
            if (utcNow.HasValue)
            {
                shouldBlock = ShouldBlockForUtcTime(utcNow.Value);
            }
            else if (FailClosedWhenTimeUnavailable)
            {
                shouldBlock = true;
            }

            if (!shouldBlock)
            {
                return;
            }

            foreach (string processName in MonitoredProcessNames)
            {
                Process[] processes;
                try
                {
                    processes = Process.GetProcessesByName(processName);
                }
                catch (Exception ex)
                {
                    LogError($"Failed to enumerate processes for {processName}.", ex);
                    continue;
                }

                foreach (Process process in processes)
                {
                    using (process)
                    {
                        try { process.CloseMainWindow(); } catch (Exception) { }
                        try { process.Kill(); } catch (Exception) { }
                    }
                }
            }
        }

        private static bool ShouldBlockForUtcTime(DateTimeOffset utcNow)
        {
            DateTimeOffset localNow = OnlineTimeProvider.ConvertUtcToPhilippinesTime(utcNow);

            if (localNow <= new DateTimeOffset(2024, 2, 24, 0, 0, 0, TimeSpan.FromHours(8)))
            {
                return false;
            }

            DayOfWeek day = localNow.DayOfWeek;
            TimeSpan time = localNow.TimeOfDay;
            TimeSpan startTime = new TimeSpan(9, 0, 0); // 9:00 AM Philippines time
            TimeSpan endTime = new TimeSpan(21, 0, 0); // 9:00 PM Philippines time

            bool allowed = (day == DayOfWeek.Saturday && time >= startTime) ||
                           (day == DayOfWeek.Sunday && time <= endTime);

            return !allowed;
        }

        private static string NormalizeProcessName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            string trimmed = name.Trim();
            return trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(trimmed)
                : trimmed;
        }

        private static void LogError(string message, Exception ex)
        {
            try
            {
                WriteEventLogError($"{message}{Environment.NewLine}{ex}");
            }
            catch (Exception)
            {
                // Best-effort logging only.
            }
        }

        private static void WriteEventLogError(string message)
        {
            try
            {
                if (!EventLog.SourceExists(EventSourceName))
                {
                    try
                    {
                        EventLog.CreateEventSource(EventSourceName, EventLogName);
                    }
                    catch (Exception)
                    {
                        // If we cannot create the source, fall back to Trace.
                        Trace.WriteLine($"{DateTimeOffset.UtcNow:o} {message}");
                        return;
                    }
                }

                string entry = message;
                if (entry.Length > 32000)
                {
                    entry = entry.Substring(0, 32000);
                }

                EventLog.WriteEntry(EventSourceName, entry, EventLogEntryType.Error);
            }
            catch (Exception)
            {
                Trace.WriteLine($"{DateTimeOffset.UtcNow:o} {message}");
            }
        }

        private static void ScheduleImmediateTick()
        {
            try
            {
                timer?.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Ignore shutdown race.
            }
        }
    }

    internal static class OnlineTimeProvider
    {
        private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan NtpTimeout = TimeSpan.FromSeconds(2);
        private static readonly TimeSpan MaxAllowedSkew = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan MinRetryDelay = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

        private static readonly string[] NtpServers =
        {
            "time.google.com",
            "time.cloudflare.com",
            "pool.ntp.org"
        };

        private static readonly string[] HttpTimeEndpoints =
        {
            "https://worldtimeapi.org/api/timezone/Etc/UTC",
            "https://timeapi.io/api/Time/current/zone?timeZone=UTC"
        };

        private static readonly HttpClient HttpClient = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        })
        {
            Timeout = HttpTimeout
        };

        private static readonly SemaphoreSlim SyncGate = new SemaphoreSlim(1, 1);
        private static readonly object CacheLock = new object();
        private static readonly object TimeZoneLock = new object();
        private static Stopwatch syncStopwatch = new Stopwatch();
        private static Stopwatch retryStopwatch = new Stopwatch();
        private static TimeSpan currentRetryDelay = TimeSpan.Zero;
        private static DateTimeOffset? lastNetworkUtc;
        private static TimeZoneInfo philippinesTimeZone;

        public static async Task<DateTimeOffset?> GetUtcNowAsync()
        {
            if (!NeedsSync() && TryGetCachedUtc(out DateTimeOffset cachedUtc))
            {
                return cachedUtc;
            }

            await SyncGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (NeedsSync())
                {
                    DateTimeOffset? syncedUtc = await TryFetchNetworkUtcAsync().ConfigureAwait(false);
                    if (syncedUtc.HasValue)
                    {
                        UpdateCache(syncedUtc.Value);
                    }
                }
            }
            finally
            {
                SyncGate.Release();
            }

            return TryGetCachedUtc(out DateTimeOffset utcNow) ? utcNow : (DateTimeOffset?)null;
        }

        public static void ForceResync()
        {
            lock (CacheLock)
            {
                lastNetworkUtc = null;
                syncStopwatch.Reset();
                retryStopwatch.Reset();
                currentRetryDelay = TimeSpan.Zero;
            }
        }

        public static DateTimeOffset ConvertUtcToPhilippinesTime(DateTimeOffset utcNow)
        {
            TimeZoneInfo tz = GetPhilippinesTimeZone();
            return TimeZoneInfo.ConvertTime(utcNow, tz);
        }

        private static bool NeedsSync()
        {
            lock (CacheLock)
            {
                if (lastNetworkUtc == null || syncStopwatch.Elapsed >= SyncInterval)
                {
                    if (retryStopwatch.IsRunning && retryStopwatch.Elapsed < currentRetryDelay)
                    {
                        return false;
                    }

                    return true;
                }

                return false;
            }
        }

        private static bool TryGetCachedUtc(out DateTimeOffset utcNow)
        {
            lock (CacheLock)
            {
                if (!lastNetworkUtc.HasValue)
                {
                    utcNow = default;
                    return false;
                }

                utcNow = lastNetworkUtc.Value + syncStopwatch.Elapsed;
                return true;
            }
        }

        private static void UpdateCache(DateTimeOffset networkUtc)
        {
            lock (CacheLock)
            {
                lastNetworkUtc = networkUtc;
                syncStopwatch.Restart();
                currentRetryDelay = TimeSpan.Zero;
                retryStopwatch.Reset();
            }
        }

        private static async Task<DateTimeOffset?> TryFetchNetworkUtcAsync()
        {
            foreach (string server in NtpServers)
            {
                DateTimeOffset? ntpUtc = await TryGetNtpUtcAsync(server).ConfigureAwait(false);
                if (ntpUtc.HasValue && IsReasonable(ntpUtc.Value))
                {
                    return ntpUtc.Value;
                }
            }

            foreach (string endpoint in HttpTimeEndpoints)
            {
                DateTimeOffset? httpUtc = await TryGetHttpUtcAsync(endpoint).ConfigureAwait(false);
                if (httpUtc.HasValue && IsReasonable(httpUtc.Value))
                {
                    return httpUtc.Value;
                }
            }

            RegisterSyncFailure();
            return null;
        }

        private static void RegisterSyncFailure()
        {
            lock (CacheLock)
            {
                if (currentRetryDelay == TimeSpan.Zero)
                {
                    currentRetryDelay = MinRetryDelay;
                }
                else
                {
                    double nextMinutes = Math.Min(currentRetryDelay.TotalMinutes * 2, MaxRetryDelay.TotalMinutes);
                    currentRetryDelay = TimeSpan.FromMinutes(nextMinutes);
                }

                retryStopwatch.Restart();
            }
        }

        private static async Task<DateTimeOffset?> TryGetNtpUtcAsync(string server)
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    udp.Client.ReceiveTimeout = (int)NtpTimeout.TotalMilliseconds;
                    udp.Client.SendTimeout = (int)NtpTimeout.TotalMilliseconds;
                    udp.Connect(server, 123);

                    byte[] request = new byte[48];
                    request[0] = 0x1B; // NTP client request
                    await udp.SendAsync(request, request.Length).ConfigureAwait(false);

                    Task<UdpReceiveResult> receiveTask = udp.ReceiveAsync();
                    Task completed = await Task.WhenAny(receiveTask, Task.Delay(NtpTimeout)).ConfigureAwait(false);
                    if (completed != receiveTask)
                    {
                        _ = receiveTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                        return null;
                    }

                    byte[] buffer = receiveTask.Result.Buffer;
                    if (buffer == null || buffer.Length < 48)
                    {
                        return null;
                    }

                    ulong intPart = ((ulong)buffer[40] << 24) |
                                    ((ulong)buffer[41] << 16) |
                                    ((ulong)buffer[42] << 8) |
                                    buffer[43];
                    ulong fractPart = ((ulong)buffer[44] << 24) |
                                      ((ulong)buffer[45] << 16) |
                                      ((ulong)buffer[46] << 8) |
                                      buffer[47];
                    const ulong NtpEpoch = 2208988800UL;
                    if (intPart < NtpEpoch)
                    {
                        return null;
                    }

                    double seconds = (intPart - NtpEpoch) + (fractPart / 4294967296.0);
                    long unixSeconds = (long)Math.Floor(seconds);
                    if (unixSeconds <= 0)
                    {
                        return null;
                    }

                    return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static async Task<DateTimeOffset?> TryGetHttpUtcAsync(string endpoint)
        {
            try
            {
                using (var response = await HttpClient.GetAsync(endpoint).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        return null;
                    }

                    string content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (TryParseUtcFromJson(content, out DateTimeOffset utc))
                    {
                        return utc;
                    }
                }
            }
            catch (Exception)
            {
                return null;
            }

            return null;
        }

        private static bool TryParseUtcFromJson(string json, out DateTimeOffset utc)
        {
            utc = default;

            string[] patterns =
            {
                "\"utc_datetime\"\\s*:\\s*\"(?<dt>[^\"]+)\"",
                "\"dateTime\"\\s*:\\s*\"(?<dt>[^\"]+)\""
            };

            foreach (string pattern in patterns)
            {
                Match match = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                {
                    continue;
                }

                string raw = match.Groups["dt"].Value;
                if (DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out utc))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsReasonable(DateTimeOffset candidate)
        {
            lock (CacheLock)
            {
                if (!lastNetworkUtc.HasValue)
                {
                    return true;
                }

                DateTimeOffset expected = lastNetworkUtc.Value + syncStopwatch.Elapsed;
                TimeSpan delta = (candidate - expected).Duration();
                return delta <= MaxAllowedSkew;
            }
        }

        private static TimeZoneInfo GetPhilippinesTimeZone()
        {
            if (philippinesTimeZone != null)
            {
                return philippinesTimeZone;
            }

            lock (TimeZoneLock)
            {
                if (philippinesTimeZone != null)
                {
                    return philippinesTimeZone;
                }

                try
                {
                    philippinesTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Singapore Standard Time");
                }
                catch (TimeZoneNotFoundException)
                {
                    philippinesTimeZone = TimeZoneInfo.CreateCustomTimeZone(
                        "Philippines Standard Time",
                        TimeSpan.FromHours(8),
                        "Philippines Standard Time",
                        "Philippines Standard Time");
                }
                catch (InvalidTimeZoneException)
                {
                    philippinesTimeZone = TimeZoneInfo.CreateCustomTimeZone(
                        "Philippines Standard Time",
                        TimeSpan.FromHours(8),
                        "Philippines Standard Time",
                        "Philippines Standard Time");
                }

                return philippinesTimeZone;
            }
        }
    }
}
