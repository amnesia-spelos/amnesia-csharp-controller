using System.Globalization;

namespace AmnesiaController.Core;

/// <summary>
/// Pose and Body Recording and Playback: the Controller's explicit protocol automation. Only one
/// recording or playback, of either kind, is active at a time; each kind keeps its own buffer.
/// A Body Recording also records interaction and break Events as the Commands that reproduce them.
/// </summary>
public sealed partial class Controller
{
    private const decimal MaxRecordingSeconds = 3600;

    private const string DefaultAvatarId = "a1";

    // A wakeup later than this stretches the rest of the playback; a less late one is caught up on
    // the next send, so timer granularity does not add up over a playback.
    private static readonly TimeSpan MaxPlaybackLateness = TimeSpan.FromMilliseconds(100);

    private static readonly RecordingKind PoseKind = new("Pose", "/pose-record", "Local Pose State Update", RecordedSample.TryParseLocalPose, null);

    private static readonly RecordingKind BodyKind =
        new("Body", "/bodies-record", "Reported Bodies State Update", RecordedSample.TryParseReportedBodies, RecordedSample.TryParseInteractionEvent);

    private readonly Dictionary<RecordingKind, IReadOnlyList<RecordedSample>> _bufferByKind = [];
    private Recording? _recording;
    private Playback? _playback;
    private long _lastWakeupToken;

    /// <summary>A wakeup requested by <see cref="ScheduleWakeup"/> is due; stale tokens are ignored.</summary>
    public IReadOnlyList<ControllerEffect> WakeupDue(long token, DateTime now)
    {
        if (_playback is not { } playback || playback.WakeupToken != token)
            return [];

        return SendDueSamples(playback, now);
    }

    private IReadOnlyList<ControllerEffect> Record(RecordingKind kind, string argument, DateTime now)
    {
        // Digits at both ends rule out ".5" and "5.", matching how the protocol writes numbers.
        if (argument.Length == 0 || !char.IsAsciiDigit(argument[0]) || !char.IsAsciiDigit(argument[^1])
            || !decimal.TryParse(argument, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            || seconds is <= 0 or > MaxRecordingSeconds)
            return [Notice(now, $"Invalid duration '{argument}'; expected seconds of game time above 0 and at most {MaxRecordingSeconds}, such as 5 or 0.5.")];

        if (RejectStart(kind.RecordingName, now) is { } rejected)
            return [rejected];

        _recording = new Recording(kind, seconds * 1000);
        return [Notice(now, $"{kind.RecordingName} armed for {argument} s of game time; waiting for the first {kind.StateUpdate}.")];
    }

    /// <summary>An operation starts only in a Session and never replaces the active one.</summary>
    private ShowLine? RejectStart(string operation, DateTime now)
    {
        if (!_connected)
            return Notice(now, $"Not connected; {operation} not started.");

        return ActiveOperation() is { } active
            ? Notice(now, $"{active} is already active; nothing was started.")
            : null;
    }

    private IReadOnlyList<ControllerEffect> RecordStop(RecordingKind kind, DateTime now)
    {
        if (_recording is not { } recording || recording.Kind != kind)
            return [Notice(now, $"No {kind.RecordingName} is active.")];

        if (recording.Samples.Count == 0)
            return [CancelRecording("before its first sample", now)];

        return [CompleteRecording(recording, "stopped early", now)];
    }

    private IReadOnlyList<ControllerEffect> RecordCancel(RecordingKind kind, DateTime now) =>
        _recording?.Kind != kind
            ? [Notice(now, $"No {kind.RecordingName} is active.")]
            : [CancelRecording("as requested", now)];

    private IReadOnlyList<ControllerEffect> PosePlay(string argument, DateTime now)
    {
        var avatarId = argument.Length == 0 ? DefaultAvatarId : argument;
        if (!IsAvatarId(avatarId))
            return [Notice(now, $"Invalid Avatar Identifier '{argument}'; expected one 1–32 character ID of printable ASCII without ':'. Nothing was sent.")];

        return Play(PoseKind, $"avatarpose {avatarId} ", $" into {avatarId}", now);
    }

    /// <summary>The Entity and Body Identifiers and the map are in the recorded payload, so there is nothing to choose.</summary>
    private IReadOnlyList<ControllerEffect> BodiesPlay(string argument, DateTime now) =>
        argument.Length != 0
            ? [Notice(now, $"/bodies-play takes no argument, but got '{argument}'. Nothing was sent.")]
            : Play(BodyKind, "entitybodies ", "", now);

    private IReadOnlyList<ControllerEffect> Play(RecordingKind kind, string commandPrefix, string target, DateTime now)
    {
        if (RejectStart(kind.PlaybackName, now) is { } rejected)
            return [rejected];

        if (!_bufferByKind.TryGetValue(kind, out var samples))
            return [Notice(now, $"No {kind.BufferName}; record one with {kind.RecordDirective} first.")];

        var playback = new Playback(kind, commandPrefix, target, samples, now);
        _playback = playback;
        return [Notice(now, $"{kind.PlaybackName} started: {Describe(samples)}{target}."), .. SendDueSamples(playback, now)];
    }

    private IReadOnlyList<ControllerEffect> PlayStop(RecordingKind kind, DateTime now)
    {
        if (_playback is not { } playback || playback.Kind != kind)
            return [Notice(now, $"No {kind.PlaybackName} is active.")];

        _playback = null;
        return [Notice(now, $"{kind.PlaybackName} stopped after {playback.Progress}; the {kind.BufferName} is kept.")];
    }

    /// <summary>Sends the next sample and any that share its game time, then waits until the next one is due.</summary>
    private IReadOnlyList<ControllerEffect> SendDueSamples(Playback playback, DateTime now)
    {
        var effects = new List<ControllerEffect>();
        var samples = playback.Samples;
        var lateness = now - playback.DueAt(playback.SentCount);
        if (lateness > MaxPlaybackLateness)
            playback.ScheduleStart += lateness;
        do
        {
            var sample = samples[playback.SentCount];
            effects.AddRange(Send(sample.IsEvent ? sample.Payload : playback.CommandPrefix + sample.Payload, now));
            playback.SentCount++;
        }
        while (playback.SentCount < samples.Count && samples[playback.SentCount].TimeMs == samples[playback.SentCount - 1].TimeMs);

        if (playback.SentCount == samples.Count)
        {
            _playback = null;
            effects.Add(Notice(now, $"{playback.Kind.PlaybackName} complete: {CountSamplesAndEvents(samples)}, {FormatSeconds((decimal)(now - playback.StartedAt).TotalMilliseconds)} s{playback.Target}."));
            return effects;
        }

        playback.WakeupToken = ++_lastWakeupToken;
        effects.Add(new ScheduleWakeup(playback.DueAt(playback.SentCount), playback.WakeupToken));
        return effects;
    }

    private static bool IsAvatarId(string id) => id.Length is >= 1 and <= 32 && id.All(c => c is >= '!' and <= '~' and not ':');

    /// <summary>
    /// Records a State Update of the active recording's kind, or an Event it records. An Event carries no game time,
    /// so it takes that of the State Update before it, or of the first one if it arrives while the recording is armed.
    /// </summary>
    private IReadOnlyList<ControllerEffect> ObserveRecordedLine(string wireLine, DateTime now)
    {
        if (_recording is not { } recording)
            return [];

        if (recording.Kind.TryParseEvent(wireLine, out var command))
        {
            recording.AddEvent(command);
            return [];
        }

        if (!recording.Kind.TryParse(wireLine, out var sample))
            return [];

        if (recording.Samples.Count > 0 && sample.TimeMs < recording.Samples[^1].TimeMs)
            return [CancelRecording($"because game time went backwards from {recording.Samples[^1].TimeMs} ms to {sample.TimeMs} ms", now)];

        var first = recording.Samples.Count == 0;
        recording.AddSample(sample);
        if (first)
            return [Notice(now, $"{recording.Kind.RecordingName} started at game time {sample.TimeMs} ms.")];

        return recording.OffsetMs < recording.DurationMs
            ? []
            : [CompleteRecording(recording, "complete", now)];
    }

    /// <summary>Ends the active operation because its Session ended; every buffer is kept.</summary>
    private IReadOnlyList<ControllerEffect> CancelOperationOnDisconnect(DateTime now)
    {
        if (_recording is not null)
            return [CancelRecording("by disconnection", now)];

        if (_playback is { } playback)
        {
            _playback = null;
            return [Notice(now, $"{playback.Kind.PlaybackName} cancelled by disconnection after {playback.Progress}; the {playback.Kind.BufferName} is kept.")];
        }

        return [];
    }

    private string? ActiveOperation() => _recording?.Kind.RecordingName ?? _playback?.Kind.PlaybackName;

    private ShowLine CompleteRecording(Recording recording, string outcome, DateTime now)
    {
        _recording = null;
        _bufferByKind[recording.Kind] = recording.Samples;
        return Notice(now, $"{recording.Kind.RecordingName} {outcome}: {Describe(recording.Samples)}.");
    }

    private ShowLine CancelRecording(string reason, DateTime now)
    {
        var kind = _recording!.Kind;
        _recording = null;
        var kept = _bufferByKind.ContainsKey(kind) ? $"the previous {kind.BufferName} is kept" : $"no {kind.BufferName} exists";
        return Notice(now, $"{kind.RecordingName} cancelled {reason}; {kept}.");
    }

    /// <summary>Reports the active operation, of either kind, and the buffer of <paramref name="kind"/>.</summary>
    private IReadOnlyList<ControllerEffect> Status(RecordingKind kind, DateTime now)
    {
        var operation = (_recording, _playback) switch
        {
            ({ Samples.Count: 0 } recording, _) =>
                $"{recording.Kind.RecordingName} armed for {FormatSeconds(recording.DurationMs)} s of game time; waiting for the first {recording.Kind.StateUpdate}.",
            ({ } recording, _) =>
                $"{recording.Kind.RecordingName} started: {CountSamplesAndEvents(recording.Samples)}, {FormatSeconds(recording.OffsetMs)} of {FormatSeconds(recording.DurationMs)} s of game time; waiting for the boundary sample.",
            (_, { } playback) =>
                $"{playback.Kind.PlaybackName}{playback.Target}: {playback.Progress} sent.",
            _ => "Recording and playback are idle.",
        };
        var buffer = _bufferByKind.TryGetValue(kind, out var samples)
            ? $"{kind.BufferName}: {Describe(samples)}."
            : $"No {kind.BufferName}.";

        return [Notice(now, operation), Notice(now, buffer)];
    }

    private static string Describe(IReadOnlyList<RecordedSample> samples) =>
        $"{CountSamplesAndEvents(samples)}, {FormatSeconds(samples[^1].TimeMs - samples[0].TimeMs)} s of game time";

    /// <summary>Counts State Updates as samples, and names Events only when there are any.</summary>
    private static string CountSamplesAndEvents(IReadOnlyCollection<RecordedSample> samples)
    {
        var events = samples.Count(sample => sample.IsEvent);
        var counted = $"{samples.Count - events} sample(s)";
        return events == 0 ? counted : $"{counted} and {events} Event(s)";
    }

    private static string FormatSeconds(decimal ms) => (ms / 1000).ToString("0.000", CultureInfo.InvariantCulture);

    private delegate bool SampleParser(string wireLine, out RecordedSample sample);

    private delegate bool EventParser(string wireLine, out string command);

    /// <summary>
    /// What a recording records and how it names itself: Local Pose or Reported Bodies State Updates,
    /// and any Events that <paramref name="tryParseEvent"/> turns into Commands.
    /// </summary>
    private sealed class RecordingKind(string name, string recordDirective, string stateUpdate, SampleParser tryParse, EventParser? tryParseEvent)
    {
        public string RecordingName { get; } = $"{name} Recording";

        public string PlaybackName { get; } = $"{name} Playback";

        public string BufferName { get; } = $"{name} Recording Buffer";

        public string RecordDirective { get; } = recordDirective;

        public string StateUpdate { get; } = stateUpdate;

        public bool TryParse(string wireLine, out RecordedSample sample) => tryParse(wireLine, out sample);

        public bool TryParseEvent(string wireLine, out string command)
        {
            command = "";
            return tryParseEvent is not null && tryParseEvent(wireLine, out command);
        }
    }

    private sealed class Recording(RecordingKind kind, decimal durationMs)
    {
        public RecordingKind Kind { get; } = kind;

        public decimal DurationMs { get; } = durationMs;

        private readonly List<string> _commandsWhileArmed = [];

        /// <summary>State Updates and the Events among them, in the order received; empty while armed.</summary>
        public List<RecordedSample> Samples { get; } = [];

        public ulong OffsetMs => Samples[^1].TimeMs - Samples[0].TimeMs;

        public void AddEvent(string command)
        {
            if (Samples.Count == 0)
                _commandsWhileArmed.Add(command);
            else
                Samples.Add(new RecordedSample(Samples[^1].TimeMs, command, IsEvent: true));
        }

        public void AddSample(RecordedSample sample)
        {
            Samples.AddRange(_commandsWhileArmed.Select(command => new RecordedSample(sample.TimeMs, command, IsEvent: true)));
            _commandsWhileArmed.Clear();
            Samples.Add(sample);
        }
    }

    /// <param name="commandPrefix">Prepended to each recorded payload to make the Command sent.</param>
    /// <param name="target">Appended to notices to name where the samples go, such as <c> into a1</c>.</param>
    private sealed class Playback(RecordingKind kind, string commandPrefix, string target, IReadOnlyList<RecordedSample> samples, DateTime startedAt)
    {
        public RecordingKind Kind { get; } = kind;

        public string CommandPrefix { get; } = commandPrefix;

        public string Target { get; } = target;

        public IReadOnlyList<RecordedSample> Samples { get; } = samples;

        public DateTime StartedAt { get; } = startedAt;

        /// <summary>When the first sample is due; later ones are due their recorded game time after it.</summary>
        public DateTime ScheduleStart { get; set; } = startedAt;

        public DateTime DueAt(int index)
        {
            double offsetMs = Samples[index].TimeMs - Samples[0].TimeMs;
            return offsetMs < (DateTime.MaxValue - ScheduleStart).TotalMilliseconds
                ? ScheduleStart.AddMilliseconds(offsetMs)
                : DateTime.MaxValue;
        }

        public int SentCount { get; set; }

        public long WakeupToken { get; set; }

        public string Progress
        {
            get
            {
                var events = Samples.Count(sample => sample.IsEvent);
                var sentEvents = Samples.Take(SentCount).Count(sample => sample.IsEvent);
                var progress = $"{SentCount - sentEvents} of {Samples.Count - events} sample(s)";
                return events == 0 ? progress : $"{progress} and {sentEvents} of {events} Event(s)";
            }
        }
    }
}
