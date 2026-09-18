using System.Globalization;

namespace AmnesiaController.Core;

/// <summary>Pose Recording and Pose Playback: the Controller's explicit Local Pose automation.</summary>
public sealed partial class Controller
{
    private const decimal MaxRecordingSeconds = 3600;

    private const string DefaultAvatarId = "a1";

    private PoseRecording? _poseRecording;
    private PosePlayback? _posePlayback;
    private IReadOnlyList<PoseSample>? _poseRecordingBuffer;
    private long _lastWakeupToken;

    /// <summary>A wakeup requested by <see cref="ScheduleWakeup"/> is due; stale tokens are ignored.</summary>
    public IReadOnlyList<ControllerEffect> WakeupDue(long token, DateTime now)
    {
        if (_posePlayback is not { } playback || playback.WakeupToken != token)
            return [];

        return SendDuePoses(playback, now);
    }

    private IReadOnlyList<ControllerEffect> PoseRecord(string argument, DateTime now)
    {
        // Digits at both ends rule out ".5" and "5.", matching how the protocol writes numbers.
        if (argument.Length == 0 || !char.IsAsciiDigit(argument[0]) || !char.IsAsciiDigit(argument[^1])
            || !decimal.TryParse(argument, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds is <= 0 or > MaxRecordingSeconds)
            return [Notice(now, $"Invalid duration '{argument}'; expected seconds of game time above 0 and at most {MaxRecordingSeconds}, such as 5 or 0.5.")];

        if (RejectPoseStart("Pose Recording", now) is { } rejected)
            return [rejected];

        _poseRecording = new PoseRecording(seconds * 1000);
        return [Notice(now, $"Pose Recording armed for {argument} s of game time; waiting for the first Local Pose State Update.")];
    }

    /// <summary>A pose operation starts only in a Session and never replaces the active one.</summary>
    private ShowLine? RejectPoseStart(string operation, DateTime now)
    {
        if (!_connected)
            return Notice(now, $"Not connected; {operation} not started.");

        return ActivePoseOperation() is { } active
            ? Notice(now, $"{active} is already active; nothing was started.")
            : null;
    }

    private IReadOnlyList<ControllerEffect> PoseRecordStop(DateTime now)
    {
        if (_poseRecording is not { } recording)
            return [Notice(now, "No Pose Recording is active.")];

        if (recording.Samples.Count == 0)
            return [CancelPoseRecording(now, "before its first sample")];

        return [CompletePoseRecording(recording, "stopped early", now)];
    }

    private IReadOnlyList<ControllerEffect> PoseRecordCancel(DateTime now) =>
        _poseRecording is null
            ? [Notice(now, "No Pose Recording is active.")]
            : [CancelPoseRecording(now, "as requested")];

    private IReadOnlyList<ControllerEffect> PosePlay(string argument, DateTime now)
    {
        var avatarId = argument.Length == 0 ? DefaultAvatarId : argument;
        if (!IsAvatarId(avatarId))
            return [Notice(now, $"Invalid Avatar Identifier '{argument}'; expected one 1–32 character ID of printable ASCII without ':'. Nothing was sent.")];

        if (RejectPoseStart("Pose Playback", now) is { } rejected)
            return [rejected];

        if (_poseRecordingBuffer is not { } samples)
            return [Notice(now, "No Pose Recording Buffer; record one with /pose-record first.")];

        var playback = new PosePlayback(avatarId, samples, now);
        _posePlayback = playback;
        return [Notice(now, $"Pose Playback started: {Describe(samples)} into {avatarId}."), .. SendDuePoses(playback, now)];
    }

    private IReadOnlyList<ControllerEffect> PosePlayStop(DateTime now)
    {
        if (_posePlayback is not { } playback)
            return [Notice(now, "No Pose Playback is active.")];

        _posePlayback = null;
        return [Notice(now, $"Pose Playback stopped after {playback.Progress}; the Pose Recording Buffer is kept.")];
    }

    /// <summary>Sends the next pose and any that share its game time, then waits the recorded delta from now.</summary>
    private IReadOnlyList<ControllerEffect> SendDuePoses(PosePlayback playback, DateTime now)
    {
        var effects = new List<ControllerEffect>();
        var samples = playback.Samples;
        do
        {
            effects.AddRange(Send($"avatarpose {playback.AvatarId} {samples[playback.SentCount].Payload}", now));
            playback.SentCount++;
        }
        while (playback.SentCount < samples.Count && samples[playback.SentCount].TimeMs == samples[playback.SentCount - 1].TimeMs);

        if (playback.SentCount == samples.Count)
        {
            _posePlayback = null;
            effects.Add(Notice(now, $"Pose Playback complete: {samples.Count} sample(s), {FormatSeconds((decimal)(now - playback.StartedAt).TotalMilliseconds)} s into {playback.AvatarId}."));
            return effects;
        }

        playback.WakeupToken = ++_lastWakeupToken;
        double deltaMs = samples[playback.SentCount].TimeMs - samples[playback.SentCount - 1].TimeMs;
        var at = deltaMs < (DateTime.MaxValue - now).TotalMilliseconds ? now.AddMilliseconds(deltaMs) : DateTime.MaxValue;
        effects.Add(new ScheduleWakeup(at, playback.WakeupToken));
        return effects;
    }

    private static bool IsAvatarId(string id) => id.Length is >= 1 and <= 32 && id.All(c => c is >= '!' and <= '~' and not ':');

    private IReadOnlyList<ControllerEffect> ObservePose(string wireLine, DateTime now)
    {
        if (_poseRecording is not { } recording || !PoseSample.TryParse(wireLine, out var sample))
            return [];

        if (recording.Samples.Count > 0 && sample.TimeMs < recording.Samples[^1].TimeMs)
            return [CancelPoseRecording(now, $"because game time went backwards from {recording.Samples[^1].TimeMs} ms to {sample.TimeMs} ms")];

        recording.Samples.Add(sample);
        if (recording.Samples.Count == 1)
            return [Notice(now, $"Pose Recording started at game time {sample.TimeMs} ms.")];

        return recording.OffsetMs < recording.DurationMs
            ? []
            : [CompletePoseRecording(recording, "complete", now)];
    }

    /// <summary>Ends the active pose operation because its Session ended; the Pose Recording Buffer is kept.</summary>
    private IReadOnlyList<ControllerEffect> CancelPoseOperationOnDisconnect(DateTime now)
    {
        if (_poseRecording is not null)
            return [CancelPoseRecording(now, "by disconnection")];

        if (_posePlayback is { } playback)
        {
            _posePlayback = null;
            return [Notice(now, $"Pose Playback cancelled by disconnection after {playback.Progress}; the Pose Recording Buffer is kept.")];
        }

        return [];
    }

    private string? ActivePoseOperation() =>
        _poseRecording is not null ? "Pose Recording"
        : _posePlayback is not null ? "Pose Playback"
        : null;

    private ShowLine CompletePoseRecording(PoseRecording recording, string outcome, DateTime now)
    {
        _poseRecording = null;
        _poseRecordingBuffer = recording.Samples;
        return Notice(now, $"Pose Recording {outcome}: {Describe(recording.Samples)}.");
    }

    private ShowLine CancelPoseRecording(DateTime now, string reason)
    {
        _poseRecording = null;
        var kept = _poseRecordingBuffer is null ? "no Pose Recording Buffer exists" : "the previous Pose Recording Buffer is kept";
        return Notice(now, $"Pose Recording cancelled {reason}; {kept}.");
    }

    private IReadOnlyList<ControllerEffect> PoseStatus(DateTime now)
    {
        var operation = (_poseRecording, _posePlayback) switch
        {
            ({ Samples.Count: 0 } recording, _) =>
                $"Pose Recording armed for {FormatSeconds(recording.DurationMs)} s of game time; waiting for the first Local Pose State Update.",
            ({ } recording, _) =>
                $"Pose Recording started: {recording.Samples.Count} sample(s), {FormatSeconds(recording.OffsetMs)} of {FormatSeconds(recording.DurationMs)} s of game time; waiting for the boundary sample.",
            (_, { } playback) =>
                $"Pose Playback into {playback.AvatarId}: {playback.Progress} sent.",
            _ => "Pose automation is idle.",
        };
        var buffer = _poseRecordingBuffer is { } samples
            ? $"Pose Recording Buffer: {Describe(samples)}."
            : "No Pose Recording Buffer.";

        return [Notice(now, operation), Notice(now, buffer)];
    }

    private static string Describe(IReadOnlyList<PoseSample> samples) =>
        $"{samples.Count} sample(s), {FormatSeconds(samples[^1].TimeMs - samples[0].TimeMs)} s of game time";

    private static string FormatSeconds(decimal ms) => (ms / 1000).ToString("0.000", CultureInfo.InvariantCulture);

    private sealed class PoseRecording(decimal durationMs)
    {
        public decimal DurationMs { get; } = durationMs;

        public List<PoseSample> Samples { get; } = [];

        public ulong OffsetMs => Samples[^1].TimeMs - Samples[0].TimeMs;
    }

    private sealed class PosePlayback(string avatarId, IReadOnlyList<PoseSample> samples, DateTime startedAt)
    {
        public string AvatarId { get; } = avatarId;

        public IReadOnlyList<PoseSample> Samples { get; } = samples;

        public DateTime StartedAt { get; } = startedAt;

        public int SentCount { get; set; }

        public long WakeupToken { get; set; }

        public string Progress => $"{SentCount} of {Samples.Count} sample(s)";
    }
}
