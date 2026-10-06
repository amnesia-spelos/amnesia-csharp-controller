using AmnesiaController.Core;

namespace AmnesiaController.Tests;

/// <summary>Interaction and break Events recorded with a Body Recording and played back as Commands.</summary>
public class BodyRecordingEventTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private const string Map = "custom_stories/My Story: Part 2/maps/cellar one.map";

    private const string State = "1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -9.8000 0.0000 90.0000 0.0000";

    private static string Bodies(ulong timeMs) => $"STATE reportedbodies {timeMs} 1 12 1 {State} {Map}";

    private static Controller RecordingController()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        controller.LineEntered("/bodies-record 3600", T0);
        return controller;
    }

    /// <summary>Records <paramref name="lines"/> into a Body Recording Buffer, then stops the recording.</summary>
    private static Controller ControllerWithBuffer(params string[] lines)
    {
        var controller = RecordingController();
        foreach (var line in lines)
            controller.WireLineReceived(line, T0);
        controller.LineEntered("/bodies-record-stop", T0);
        return controller;
    }

    private static IEnumerable<string> Notices(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<ShowLine>().Where(s => s.Direction == Direction.Notice).Select(s => s.Text);

    private static IEnumerable<string> Sent(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<SendWireLine>().Select(s => s.Line);

    private static ScheduleWakeup Wakeup(IEnumerable<ControllerEffect> effects) =>
        Assert.Single(effects.OfType<ScheduleWakeup>());

    private static string Status(Controller controller) =>
        string.Join("\n", Notices(controller.LineEntered("/bodies-status", T0)));

    [Fact]
    public void Events_play_back_as_their_Commands_right_after_the_sample_they_followed()
    {
        var controller = ControllerWithBuffer(
            Bodies(1000),
            $"EVENT interactionend 12 1 thrown {Map}",
            Bodies(1016),
            $"EVENT reportbroke 12 {State} {Map}",
            Bodies(1050));

        var first = controller.LineEntered("/bodies-play", T0);
        var second = controller.WakeupDue(Wakeup(first).Token, T0.AddMilliseconds(16));

        Assert.Equal(["entitybodies " + Bodies(1000)[21..], $"entityinteracting 12 0 {Map}"], Sent(first));
        Assert.Equal(T0.AddMilliseconds(16), Wakeup(first).At);
        Assert.Equal(["entitybodies " + Bodies(1016)[21..], $"entitybreak 12 {State} {Map}"], Sent(second));
        Assert.Equal(T0.AddMilliseconds(50), Wakeup(second).At);
    }

    [Fact]
    public void An_Event_while_armed_is_sent_just_before_the_first_sample()
    {
        var controller = RecordingController();

        var armed = controller.WireLineReceived($"EVENT interactionstart 12 1 {Map}", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(1016), T0);
        controller.LineEntered("/bodies-record-stop", T0);
        var played = controller.LineEntered("/bodies-play", T0);

        Assert.Empty(Notices(armed));
        Assert.Equal([$"entityinteracting 12 1 {Map}", "entitybodies " + Bodies(1000)[21..]], Sent(played));
    }

    [Theory]
    [InlineData("released")]
    [InlineData("thrown")]
    [InlineData("too-far")]
    [InlineData("destroyed")]
    public void Every_interaction_ending_clears_the_interacting_mark(string ending)
    {
        var controller = ControllerWithBuffer(Bodies(1000), $"EVENT interactionend 12 1 {ending} {Map}");

        Assert.Contains($"entityinteracting 12 0 {Map}", Sent(controller.LineEntered("/bodies-play", T0)));
    }

    [Theory]
    [InlineData("EVENT interactionstart 12 {Map}")]
    [InlineData("EVENT interactionstart 12 1 ")]
    [InlineData("EVENT interactionstart x 1 {Map}")]
    [InlineData("EVENT interactionstart 12 +1 {Map}")]
    [InlineData("EVENT interactionend 12 1 dropped {Map}")]
    [InlineData("EVENT interactionend 12 1 thrown")]
    [InlineData("EVENT reportbroke 12 1.0 2.0 {Map}")]
    [InlineData("EVENT reportbroke 12 1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -9.8000 0.0000 90.0000 x {Map}")]
    [InlineData("EVENT reportcontact 12 {Map}")]
    [InlineData("EVENT reportsettled 12 {Map}")]
    [InlineData("EVENT:interactionstart:12 1 {Map}")]
    public void Malformed_and_unreplayable_Events_are_shown_but_not_recorded(string template)
    {
        var line = template.Replace("{Map}", Map);
        var controller = RecordingController();
        controller.WireLineReceived(Bodies(1000), T0);

        var effects = controller.WireLineReceived(line, T0);
        controller.WireLineReceived(Bodies(1016), T0);
        controller.LineEntered("/bodies-record-stop", T0);

        Assert.Contains(effects, e => e is ShowLine { Direction: Direction.Received } shown && shown.Text == line);
        Assert.Equal(["entitybodies " + Bodies(1000)[21..]], Sent(controller.LineEntered("/bodies-play", T0)));
        Assert.DoesNotContain("Event(s)", Status(controller));
    }

    [Fact]
    public void Events_do_not_count_towards_the_duration_or_complete_the_recording()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        controller.LineEntered("/bodies-record 1", T0);
        controller.WireLineReceived(Bodies(1000), T0);

        var events = Enumerable.Range(0, 5)
            .SelectMany(_ => controller.WireLineReceived($"EVENT interactionend 12 1 released {Map}", T0.AddSeconds(5)))
            .ToList();
        var boundary = controller.WireLineReceived(Bodies(2000), T0.AddSeconds(5));

        Assert.Empty(Notices(events));
        var completed = Assert.Single(Notices(boundary));
        Assert.Contains("Body Recording complete: 2 sample(s) and 5 Event(s), 1.000 s", completed);
    }

    [Fact]
    public void Stopping_with_only_Events_recorded_cancels_the_recording()
    {
        var controller = RecordingController();
        controller.WireLineReceived($"EVENT interactionstart 12 1 {Map}", T0);

        var stopped = controller.LineEntered("/bodies-record-stop", T0);

        Assert.Contains("Body Recording cancelled before its first sample", Assert.Single(Notices(stopped)));
        Assert.Contains("No Body Recording Buffer", Status(controller));
    }

    [Fact]
    public void Cancelling_discards_Events_recorded_while_armed()
    {
        var controller = ControllerWithBuffer(Bodies(1000), Bodies(1016));
        controller.LineEntered("/bodies-record 3600", T0);
        controller.WireLineReceived($"EVENT interactionstart 12 1 {Map}", T0);

        controller.LineEntered("/bodies-record-cancel", T0);
        controller.LineEntered("/bodies-record 3600", T0);
        controller.WireLineReceived(Bodies(2000), T0);
        controller.WireLineReceived(Bodies(2016), T0);
        controller.LineEntered("/bodies-record-stop", T0);

        Assert.Equal(["entitybodies " + Bodies(2000)[21..]], Sent(controller.LineEntered("/bodies-play", T0)));
    }

    [Fact]
    public void Game_time_going_backwards_cancels_a_recording_holding_Events_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(Bodies(500), Bodies(516));
        controller.LineEntered("/bodies-record 3600", T0);
        controller.WireLineReceived($"EVENT interactionstart 12 1 {Map}", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived($"EVENT interactionend 12 1 thrown {Map}", T0);

        var cancelled = controller.WireLineReceived(Bodies(999), T0);

        Assert.Contains(Notices(cancelled), n => n.Contains("cancelled because game time went backwards"));
        Assert.Contains("Body Recording Buffer: 2 sample(s), 0.016 s", Status(controller));
    }

    [Fact]
    public void Muted_Events_are_still_recorded()
    {
        var controller = RecordingController();
        controller.LineEntered("/mute event", T0);
        controller.WireLineReceived(Bodies(1000), T0);

        var effects = controller.WireLineReceived($"EVENT interactionend 12 1 released {Map}", T0);
        controller.LineEntered("/bodies-record-stop", T0);

        Assert.Empty(effects);
        Assert.Contains("Body Recording Buffer: 1 sample(s) and 1 Event(s)", Status(controller));
    }

    [Fact]
    public void A_Pose_Recording_ignores_interaction_Events()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        controller.LineEntered("/pose-record 3600", T0);
        controller.WireLineReceived($"STATE localpose 1000 0 1.0000 2.0000 3.0000 0.0000 0.0000 0 {Map}", T0);

        controller.WireLineReceived($"EVENT interactionstart 12 1 {Map}", T0);
        controller.LineEntered("/pose-record-stop", T0);

        Assert.DoesNotContain("Event(s)", string.Join("\n", Notices(controller.LineEntered("/pose-status", T0))));
    }

    [Fact]
    public void Status_and_stop_report_Events_alongside_samples()
    {
        var controller = ControllerWithBuffer(
            $"EVENT interactionstart 12 1 {Map}",
            Bodies(1000),
            Bodies(1016),
            $"EVENT interactionend 12 1 released {Map}");
        controller.LineEntered("/bodies-play", T0);

        var status = Status(controller);
        var stopped = controller.LineEntered("/bodies-play-stop", T0);

        Assert.Contains("Body Playback: 1 of 2 sample(s) and 1 of 2 Event(s) sent", status);
        Assert.Contains("Body Recording Buffer: 2 sample(s) and 2 Event(s), 0.016 s", status);
        Assert.Contains("Body Playback stopped after 1 of 2 sample(s) and 1 of 2 Event(s)", Assert.Single(Notices(stopped)));
    }
}
