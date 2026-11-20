namespace YTDownloadTool.Core.Livestream;

public sealed record LiveEdgeWatchdogDecision(
    bool ShouldStopNow,
    string? Reason,
    double CatchUpProgressPercent,
    int? MinFragmentIndex,
    int? TargetStopFragment,
    bool HasReachedLiveEdge);

/// <summary>
/// Tracks multi-stream (--live-from-start) fragment progress for YouTube live DASH captures.
/// Ensures Mode C stops near the live edge only once ALL concurrent tracks (video + audio)
/// have reached the calculated target fragment, and includes a live-head arrival fallback
/// so Mode C never hangs indefinitely even on streams with shortened custom DVR windows.
/// </summary>
public sealed class ModeCLiveEdgeWatchdog
{
    private const double DefaultFragmentDurationSeconds = 5.0;
    private const double MaxYouTubeDvrHours = 120.0;

    private readonly DateTimeOffset? _streamStartUtc;
    private readonly int _liveEdgeMarginSeconds;
    private readonly bool _isDvrWindowCapped;
    private readonly int _expectedTrackCount;

    private readonly Dictionary<string, int> _highestFragmentByTrack = new(StringComparer.OrdinalIgnoreCase);
    private int? _initialFragmentCount;
    private double _estimatedFragmentDurationSec = DefaultFragmentDurationSeconds;
    private int? _targetStopFragment;
    private bool _triggered;

    private int _highestOverallFragment;
    private DateTimeOffset? _firstProgressUtc;
    private DateTimeOffset? _lastFastAdvanceUtc;
    private bool _observedFastCatchUpBurst;

    public ModeCLiveEdgeWatchdog(
        DateTimeOffset? streamStartUtc,
        int liveEdgeMarginSeconds,
        bool isDvrWindowCapped,
        int expectedTrackCount = 2)
    {
        _streamStartUtc = streamStartUtc;
        _liveEdgeMarginSeconds = Math.Clamp(liveEdgeMarginSeconds, 15, 300);
        _isDvrWindowCapped = isDvrWindowCapped;
        _expectedTrackCount = Math.Clamp(expectedTrackCount, 1, 2);
    }

    public bool IsTriggered => _triggered;
    public int? TargetStopFragment => _targetStopFragment;
    public double EstimatedFragmentDurationSeconds => _estimatedFragmentDurationSec;

    public LiveEdgeWatchdogDecision ObserveFragmentProgress(
        string trackKey,
        int fragmentIndex,
        int? fragmentCount,
        DateTimeOffset? nowUtc = null)
    {
        var now = nowUtc ?? DateTimeOffset.UtcNow;
        _firstProgressUtc ??= now;

        if (_initialFragmentCount is null && fragmentCount is > 0)
        {
            _initialFragmentCount = fragmentCount.Value;
            _estimatedFragmentDurationSec = EstimateFragmentDurationSeconds(_streamStartUtc, fragmentCount.Value, now);
            _targetStopFragment = ComputeTargetStopFragment(
                fragmentCount.Value,
                _estimatedFragmentDurationSec,
                _liveEdgeMarginSeconds,
                _isDvrWindowCapped);
        }

        if (fragmentIndex >= 0)
        {
            var key = string.IsNullOrWhiteSpace(trackKey) ? "default" : trackKey;
            if (!_highestFragmentByTrack.TryGetValue(key, out var existing) || fragmentIndex > existing)
            {
                _highestFragmentByTrack[key] = fragmentIndex;
            }

            if (fragmentIndex > _highestOverallFragment)
            {
                // Detect fast backlog download (> 2 fragments/sec over initial burst)
                if (_firstProgressUtc.HasValue && (now - _firstProgressUtc.Value).TotalSeconds >= 2.0)
                {
                    var avgRate = fragmentIndex / Math.Max(0.5, (now - _firstProgressUtc.Value).TotalSeconds);
                    if (avgRate >= 1.5)
                    {
                        _observedFastCatchUpBurst = true;
                        _lastFastAdvanceUtc = now;
                    }
                }

                _highestOverallFragment = fragmentIndex;
            }
        }

        var minTrackFrag = _highestFragmentByTrack.Count > 0
            ? _highestFragmentByTrack.Values.Min()
            : 0;

        var allExpectedTracksStarted = _highestFragmentByTrack.Count >= _expectedTrackCount;

        double pct = 0;
        if (_targetStopFragment is > 0)
        {
            pct = Math.Clamp((double)minTrackFrag / _targetStopFragment.Value * 100.0, 0, 100);
        }
        else if (fragmentCount is > 0)
        {
            pct = Math.Clamp((double)minTrackFrag / fragmentCount.Value * 100.0, 0, 100);
        }

        if (_triggered)
        {
            return new LiveEdgeWatchdogDecision(true, "Live-edge stop already triggered.", 100, minTrackFrag, _targetStopFragment, true);
        }

        // Primary deterministic stop: all tracks have reached the calculated target fragment
        if (_targetStopFragment is > 0
            && allExpectedTracksStarted
            && minTrackFrag >= _targetStopFragment.Value)
        {
            _triggered = true;
            return new LiveEdgeWatchdogDecision(
                ShouldStopNow: true,
                Reason: $"Reached live-edge target (segment {minTrackFrag}/{_targetStopFragment.Value}, ~{_liveEdgeMarginSeconds}s before live edge).",
                CatchUpProgressPercent: 100,
                MinFragmentIndex: minTrackFrag,
                TargetStopFragment: _targetStopFragment,
                HasReachedLiveEdge: true);
        }

        // Secondary fallback: if a shorter custom DVR window caused yt-dlp to hit live-head waiting
        // after an initial fast catch-up burst and no fast advance has occurred for 25s
        if (_observedFastCatchUpBurst
            && allExpectedTracksStarted
            && minTrackFrag >= 5
            && _lastFastAdvanceUtc.HasValue
            && (now - _lastFastAdvanceUtc.Value).TotalSeconds >= 25.0)
        {
            _triggered = true;
            return new LiveEdgeWatchdogDecision(
                ShouldStopNow: true,
                Reason: "Caught up to current live playback pace; stopping and finalizing historical recording.",
                CatchUpProgressPercent: 100,
                MinFragmentIndex: minTrackFrag,
                TargetStopFragment: _targetStopFragment,
                HasReachedLiveEdge: true);
        }

        var nearLive = _targetStopFragment is > 0 && minTrackFrag >= _targetStopFragment.Value;
        return new LiveEdgeWatchdogDecision(false, null, pct, minTrackFrag, _targetStopFragment, nearLive);
    }

    public LiveEdgeWatchdogDecision ObserveLiveHeadWaitSignal()
    {
        var minTrackFrag = _highestFragmentByTrack.Count > 0 ? _highestFragmentByTrack.Values.Min() : 0;
        if (!_triggered && _highestFragmentByTrack.Count >= _expectedTrackCount && minTrackFrag >= 2)
        {
            _triggered = true;
            return new LiveEdgeWatchdogDecision(
                ShouldStopNow: true,
                Reason: "Downloader reached the live head; stopping and finalizing recording.",
                CatchUpProgressPercent: 100,
                MinFragmentIndex: minTrackFrag,
                TargetStopFragment: _targetStopFragment,
                HasReachedLiveEdge: true);
        }

        return new LiveEdgeWatchdogDecision(false, null, 0, minTrackFrag, _targetStopFragment, false);
    }

    public static double EstimateFragmentDurationSeconds(DateTimeOffset? streamStartUtc, int initialFragmentCount, DateTimeOffset? nowUtc = null)
    {
        if (streamStartUtc.HasValue && initialFragmentCount > 10)
        {
            var elapsedSec = ((nowUtc ?? DateTimeOffset.UtcNow) - streamStartUtc.Value).TotalSeconds;
            if (elapsedSec > 30)
            {
                var estimated = elapsedSec / initialFragmentCount;
                // YouTube DASH live fragments range from 1s (ultra-low latency) to 5-6s (normal latency)
                if (estimated is >= 0.8 and <= 6.5)
                    return Math.Clamp(estimated, 1.0, 6.0);
            }
        }

        return DefaultFragmentDurationSeconds;
    }

    public static int ComputeTargetStopFragment(
        int initialFragmentCount,
        double fragmentDurationSeconds,
        int liveEdgeMarginSeconds,
        bool isDvrWindowCapped)
    {
        var safeFragDur = Math.Clamp(fragmentDurationSeconds, 1.0, 6.0);
        var marginFrags = Math.Max(3, (int)Math.Ceiling(Math.Clamp(liveEdgeMarginSeconds, 15, 300) / safeFragDur));

        var effectiveWindowFrags = initialFragmentCount;
        if (isDvrWindowCapped)
        {
            var max120hFrags = (int)Math.Floor((MaxYouTubeDvrHours * 3600.0) / safeFragDur);
            effectiveWindowFrags = Math.Min(initialFragmentCount, max120hFrags);
        }

        return Math.Max(1, effectiveWindowFrags - marginFrags);
    }
}
