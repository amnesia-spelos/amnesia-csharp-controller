using AmnesiaController.Core;

namespace AmnesiaController.Tests;

public class BodyPlaybackTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private static string Payload(ulong timeMs, int index) =>
        $"{timeMs} 2 42 -7 {index}.0000 -2.5000 3.7500 0.0000 0.7071 0.0000 0.7071 0.5000 0.0000 -9.8000 0.0000 90.0000 0.0000 "
        + "42 0 1.0000 1.0000 1.0000 0.0000 0.0000 0.0000 1.0000 0.0000 0.0000 0.0000 0.0000 0.0000 0.0000 custom_stories/My Story: Part 2/maps/cellar one.map";

    /// <summary>A connected Controller whose Body Recording Buffer holds one sample per game time, in order.</summary>
    private static Controller ControllerWithBuffer(params ulong[] times)
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        controller.LineEntered("/bodies-record 3600", T0);
        for (var i = 0; i < times.Length; i++)
            controller.WireLineReceived("STATE reportedbodies " + Payload(times[i], i), T0);
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
    public void Playback_sends_the_first_sample_immediately_as_the_recorded_payload_after_entitybodies()
    {
        var controller = ControllerWithBuffer(1000, 1016);

        var effects = controller.LineEntered("/bodies-play", T0);

        const string command =
            "entitybodies 1000 2 42 -7 0.0000 -2.5000 3.7500 0.0000 0.7071 0.0000 0.7071 0.5000 0.0000 -9.8000 0.0000 90.0000 0.0000 "
            + "42 0 1.0000 1.0000 1.0000 0.0000 0.0000 0.0000 1.0000 0.0000 0.0000 0.0000 0.0000 0.0000 0.0000 custom_stories/My Story: Part 2/maps/cellar one.map";
        Assert.Equal([command], Sent(effects));
        Assert.Contains(new ShowLine("14:30:05.123", Direction.Sent, null, command), effects);
        Assert.Contains(Notices(effects), n => n.Contains("Body Playback started: 2 sample(s)"));
        Assert.Equal(T0.AddMilliseconds(16), Wakeup(effects).At);
    }

    [Fact]
    public void Playback_sends_every_sample_in_order_paced_by_game_time_deltas_then_completes()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1050, 1100);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var second = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        var third = controller.WakeupDue(Wakeup(second).Token, T0.AddMilliseconds(50));
        var fourth = controller.WakeupDue(Wakeup(third).Token, T0.AddMilliseconds(100));

        Assert.Equal(["entitybodies " + Payload(1016, 1)], Sent(second));
        Assert.Equal(T0.AddMilliseconds(50), Wakeup(second).At);
        Assert.Equal(["entitybodies " + Payload(1050, 2)], Sent(third));
        Assert.Equal(T0.AddMilliseconds(100), Wakeup(third).At);
        Assert.Equal(["entitybodies " + Payload(1100, 3)], Sent(fourth));
        Assert.DoesNotContain(fourth, e => e is ScheduleWakeup);
        Assert.Contains("Body Playback complete: 4 sample(s), 0.100 s", Assert.Single(Notices(fourth)));
        Assert.Contains("idle", Status(controller));
    }

    [Fact]
    public void Duplicate_timestamps_are_sent_consecutively_without_delay()
    {
        var controller = ControllerWithBuffer(1000, 1000, 1020);

        var effects = controller.LineEntered("/bodies-play", T0);

        Assert.Equal(["entitybodies " + Payload(1000, 0), "entitybodies " + Payload(1000, 1)], Sent(effects));
        Assert.Equal(T0.AddMilliseconds(20), Wakeup(effects).At);
    }

    [Fact]
    public void Late_wakeup_stretches_playback_instead_of_bursting_or_skipping()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var late = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(200));

        Assert.Equal(["entitybodies " + Payload(1016, 1)], Sent(late));
        Assert.Equal(T0.AddMilliseconds(216), Wakeup(late).At);
    }

    [Fact]
    public void Slightly_late_wakeups_keep_to_the_recorded_schedule_instead_of_adding_up()
    {
        var controller = ControllerWithBuffer(1000, 1050, 1100);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var second = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(62.5));

        Assert.Equal(["entitybodies " + Payload(1050, 1)], Sent(second));
        Assert.Equal(T0.AddMilliseconds(100), Wakeup(second).At);
    }

    [Theory]
    [InlineData("/bodies-play a1")]
    [InlineData("/bodies-play 42")]
    public void Playback_takes_no_argument_and_nothing_is_sent(string directive)
    {
        var controller = ControllerWithBuffer(1000, 1016);

        var effects = controller.LineEntered(directive, T0);

        Assert.Empty(Sent(effects));
        Assert.DoesNotContain(effects, e => e is ScheduleWakeup);
        Assert.Contains("no argument", Assert.Single(Notices(effects)));
        Assert.Contains("idle", Status(controller));
    }

    [Fact]
    public void Playback_without_a_buffer_is_rejected()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);

        var effects = controller.LineEntered("/bodies-play", T0);

        Assert.Contains("No Body Recording Buffer; record one with /bodies-record first", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Playback_cannot_start_while_disconnected()
    {
        var controller = ControllerWithBuffer(1000, 1016);
        controller.ConnectionLost(T0);

        var effects = controller.LineEntered("/bodies-play", T0);

        Assert.Empty(Sent(effects));
        Assert.Contains("Not connected", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Stopping_playback_ignores_its_pending_wakeup_and_keeps_the_buffer()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var stopped = controller.LineEntered("/bodies-play-stop", T0.AddMilliseconds(5));
        var stale = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        var again = controller.LineEntered("/bodies-play", T0.AddMilliseconds(20));

        Assert.Contains("Body Playback stopped after 1 of 3 sample(s)", Assert.Single(Notices(stopped)));
        Assert.Empty(stale);
        Assert.Equal(["entitybodies " + Payload(1000, 0)], Sent(again));
    }

    [Fact]
    public void Stopping_playback_when_none_is_active_is_reported()
    {
        var controller = ControllerWithBuffer(1000, 1016);

        Assert.Contains("No Body Playback is active", Assert.Single(Notices(controller.LineEntered("/bodies-play-stop", T0))));
    }

    [Fact]
    public void Status_reports_playback_progress()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        controller.LineEntered("/bodies-play", T0);

        var status = Status(controller);

        Assert.Contains("Body Playback: 1 of 3 sample(s) sent", status);
        Assert.Contains("Body Recording Buffer: 3 sample(s)", status);
    }

    [Fact]
    public void Active_playback_suppresses_linger_exit_which_resumes_once_it_completes()
    {
        var controller = ControllerWithBuffer(1000, 6000);
        controller.InputEnded(T0);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var whilePlaying = controller.TimePassed(T0.AddMilliseconds(4000));
        controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(5000));
        var afterwards = controller.TimePassed(T0.AddMilliseconds(5050));

        Assert.Empty(whilePlaying);
        Assert.Equal([new ExitController(0)], afterwards);
    }

    [Fact]
    public void Disconnecting_cancels_playback_and_keeps_the_buffer()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        var wakeup = Wakeup(controller.LineEntered("/bodies-play", T0));

        var lost = controller.ConnectionLost(T0.AddMilliseconds(5));
        var stale = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        controller.ConnectionEstablished(T0.AddMilliseconds(20));
        var again = controller.LineEntered("/bodies-play", T0.AddMilliseconds(20));

        Assert.Contains(Notices(lost), n => n.Contains("Body Playback cancelled by disconnection after 1 of 3"));
        Assert.Empty(stale);
        Assert.Equal(["entitybodies " + Payload(1000, 0)], Sent(again));
    }
}
