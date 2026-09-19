using AmnesiaController.Core;

namespace AmnesiaController.Tests;

public class PosePlaybackTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private static string Payload(ulong timeMs, int index) =>
        $"{timeMs} 3 {index}.0000 -2.5000 3.7500 90.0000 -45.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map";

    /// <summary>A connected Controller whose Pose Recording Buffer holds one sample per game time, in order.</summary>
    private static Controller ControllerWithBuffer(params ulong[] times)
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        controller.LineEntered("/pose-record 3600", T0);
        for (var i = 0; i < times.Length; i++)
            controller.WireLineReceived("STATE localpose " + Payload(times[i], i), T0);
        controller.LineEntered("/pose-record-stop", T0);
        return controller;
    }

    private static IEnumerable<string> Notices(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<ShowLine>().Where(s => s.Direction == Direction.Notice).Select(s => s.Text);

    private static IEnumerable<string> Sent(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<SendWireLine>().Select(s => s.Line);

    private static ScheduleWakeup Wakeup(IEnumerable<ControllerEffect> effects) =>
        Assert.Single(effects.OfType<ScheduleWakeup>());

    [Fact]
    public void Playback_sends_the_first_pose_to_a1_immediately_and_shows_it_as_sent()
    {
        var controller = ControllerWithBuffer(1000, 1016);

        var effects = controller.LineEntered("/pose-play", T0);

        var command = "avatarpose a1 " + Payload(1000, 0);
        Assert.Contains(new SendWireLine(command), effects);
        Assert.Contains(new ShowLine("14:30:05.123", Direction.Sent, null, command), effects);
        Assert.Contains(Notices(effects), n => n.Contains("Pose Playback started") && n.Contains("a1"));
        Assert.Equal(T0.AddMilliseconds(16), Wakeup(effects).At);
    }

    [Fact]
    public void Playback_sends_every_pose_in_order_paced_by_game_time_deltas_then_completes()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1050, 1100);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var second = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        var third = controller.WakeupDue(Wakeup(second).Token, T0.AddMilliseconds(50));
        var fourth = controller.WakeupDue(Wakeup(third).Token, T0.AddMilliseconds(100));

        Assert.Equal(["avatarpose a1 " + Payload(1016, 1)], Sent(second));
        Assert.Equal(T0.AddMilliseconds(50), Wakeup(second).At);
        Assert.Equal(["avatarpose a1 " + Payload(1050, 2)], Sent(third));
        Assert.Equal(T0.AddMilliseconds(100), Wakeup(third).At);
        Assert.Equal(["avatarpose a1 " + Payload(1100, 3)], Sent(fourth));
        Assert.DoesNotContain(fourth, e => e is ScheduleWakeup);
        var completed = Assert.Single(Notices(fourth));
        Assert.Contains("Pose Playback complete", completed);
        Assert.Contains("4 sample(s)", completed);
        Assert.Contains("0.100 s", completed);
    }

    [Fact]
    public void Duplicate_timestamps_are_sent_consecutively_without_delay()
    {
        var controller = ControllerWithBuffer(1000, 1000, 1000, 1020);

        var effects = controller.LineEntered("/pose-play", T0);

        Assert.Equal(
            ["avatarpose a1 " + Payload(1000, 0), "avatarpose a1 " + Payload(1000, 1), "avatarpose a1 " + Payload(1000, 2)],
            Sent(effects));
        Assert.Equal(T0.AddMilliseconds(20), Wakeup(effects).At);
    }

    [Fact]
    public void Late_wakeup_stretches_playback_instead_of_bursting_or_skipping()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032, 1048);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var late = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(200));

        Assert.Equal(["avatarpose a1 " + Payload(1016, 1)], Sent(late));
        Assert.Equal(T0.AddMilliseconds(216), Wakeup(late).At);
    }

    [Fact]
    public void Slightly_late_wakeups_keep_to_the_recorded_schedule_instead_of_adding_up()
    {
        // Each wakeup lands 12.5 ms late, as a 50 ms wait does on Windows' 15.6 ms timer.
        var controller = ControllerWithBuffer(1000, 1050, 1100, 1150);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var second = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(62.5));
        var third = controller.WakeupDue(Wakeup(second).Token, T0.AddMilliseconds(112.5));

        Assert.Equal(["avatarpose a1 " + Payload(1050, 1)], Sent(second));
        Assert.Equal(T0.AddMilliseconds(100), Wakeup(second).At);
        Assert.Equal(["avatarpose a1 " + Payload(1100, 2)], Sent(third));
        Assert.Equal(T0.AddMilliseconds(150), Wakeup(third).At);
    }

    [Fact]
    public void One_sample_buffer_is_sent_once_and_completes_immediately()
    {
        var controller = ControllerWithBuffer(1000);

        var effects = controller.LineEntered("/pose-play", T0);

        Assert.Equal(["avatarpose a1 " + Payload(1000, 0)], Sent(effects));
        Assert.DoesNotContain(effects, e => e is ScheduleWakeup);
        Assert.Contains(Notices(effects), n => n.Contains("Pose Playback complete") && n.Contains("1 sample(s), 0.000 s"));
        Assert.Contains("idle", string.Join("\n", Notices(controller.LineEntered("/pose-status", T0))));
    }

    [Theory]
    [InlineData("p2")]
    [InlineData("!~")]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345")]
    public void Playback_into_an_explicit_valid_avatar_id(string avatarId)
    {
        var controller = ControllerWithBuffer(1000, 1016);

        var effects = controller.LineEntered($"/pose-play {avatarId}", T0);

        Assert.Equal([$"avatarpose {avatarId} " + Payload(1000, 0)], Sent(effects));
    }

    [Theory]
    [InlineData("/pose-play a:1")]
    [InlineData("/pose-play abcdefghijklmnopqrstuvwxyz0123456")]
    [InlineData("/pose-play a1 a2")]
    [InlineData("/pose-play é")]
    [InlineData("/pose-play a\t1")]
    public void Invalid_avatar_id_is_rejected_locally_and_nothing_is_sent(string directive)
    {
        var controller = ControllerWithBuffer(1000, 1016);

        var effects = controller.LineEntered(directive, T0);

        Assert.Empty(Sent(effects));
        Assert.DoesNotContain(effects, e => e is ScheduleWakeup);
        Assert.Contains("Avatar", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Playback_without_a_buffer_is_rejected()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);

        var effects = controller.LineEntered("/pose-play", T0);

        Assert.Contains("No Pose Recording Buffer", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Playback_cannot_start_while_disconnected()
    {
        var controller = ControllerWithBuffer(1000, 1016);
        controller.ConnectionLost(T0);

        var effects = controller.LineEntered("/pose-play", T0);

        Assert.Empty(Sent(effects));
        Assert.Contains("Not connected", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Stopping_playback_ignores_its_pending_wakeup_and_keeps_the_buffer()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var stopped = controller.LineEntered("/pose-play-stop", T0.AddMilliseconds(5));
        var stale = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        var again = controller.LineEntered("/pose-play", T0.AddMilliseconds(20));

        Assert.Contains("stopped after 1 of 3 sample(s)", Assert.Single(Notices(stopped)));
        Assert.Empty(stale);
        Assert.Equal(["avatarpose a1 " + Payload(1000, 0)], Sent(again));
    }

    [Fact]
    public void Stopping_playback_when_none_is_active_is_reported()
    {
        var controller = ControllerWithBuffer(1000, 1016);

        Assert.Contains("No Pose Playback", Assert.Single(Notices(controller.LineEntered("/pose-play-stop", T0))));
    }

    [Fact]
    public void Wakeup_with_an_unknown_token_has_no_effect()
    {
        var controller = ControllerWithBuffer(1000, 1016);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        Assert.Empty(controller.WakeupDue(wakeup.Token + 1, T0.AddMilliseconds(16)));
        Assert.NotEmpty(Sent(controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16))));
        Assert.Empty(controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(17)));
    }

    [Fact]
    public void Status_reports_playback_progress()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        controller.LineEntered("/pose-play p2", T0);

        var status = string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));

        Assert.Contains("Pose Playback into p2", status);
        Assert.Contains("1 of 3 sample(s)", status);
        Assert.Contains("Pose Recording Buffer: 3 sample(s)", status);
    }

    [Fact]
    public void Recording_and_playback_are_mutually_exclusive_and_conflicts_do_not_disturb_the_active_operation()
    {
        var controller = ControllerWithBuffer(1000, 1016);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var record = controller.LineEntered("/pose-record 5", T0);
        var play = controller.LineEntered("/pose-play", T0);
        var next = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));

        Assert.Contains("already", Assert.Single(Notices(record)));
        Assert.Contains("already", Assert.Single(Notices(play)));
        Assert.Empty(Sent(play));
        Assert.Equal(["avatarpose a1 " + Payload(1016, 1)], Sent(next));
    }

    [Fact]
    public void Playback_cannot_start_while_a_recording_is_active()
    {
        var controller = ControllerWithBuffer(1000, 1016);
        controller.LineEntered("/pose-record 5", T0);

        var play = controller.LineEntered("/pose-play", T0);

        Assert.Empty(Sent(play));
        Assert.Contains("Pose Recording is already active", Assert.Single(Notices(play)));
    }

    [Fact]
    public void Active_playback_suppresses_linger_exit_which_resumes_once_it_completes()
    {
        var controller = ControllerWithBuffer(1000, 6000);
        controller.InputEnded(T0);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var whilePlaying = controller.TimePassed(T0.AddMilliseconds(4000));
        controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(5000));
        var afterwards = controller.TimePassed(T0.AddMilliseconds(5050));

        Assert.Empty(whilePlaying);
        Assert.Equal([new ExitController(0)], afterwards);
    }

    [Fact]
    public void Active_recording_suppresses_linger_exit_which_resumes_once_it_ends()
    {
        var controller = ControllerWithBuffer(1000);
        controller.LineEntered("/pose-record 5", T0);
        controller.InputEnded(T0);

        var whileArmed = controller.TimePassed(T0.AddMilliseconds(4000));
        controller.LineEntered("/pose-record-cancel", T0.AddMilliseconds(4000));
        var afterwards = controller.TimePassed(T0.AddMilliseconds(4050));

        Assert.Empty(whileArmed);
        Assert.Equal([new ExitController(0)], afterwards);
    }

    [Fact]
    public void Disconnecting_cancels_playback_and_keeps_the_buffer()
    {
        var controller = ControllerWithBuffer(1000, 1016, 1032);
        var wakeup = Wakeup(controller.LineEntered("/pose-play", T0));

        var lost = controller.ConnectionLost(T0.AddMilliseconds(5));
        var stale = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(16));
        controller.ConnectionEstablished(T0.AddMilliseconds(20));
        var again = controller.LineEntered("/pose-play", T0.AddMilliseconds(20));

        Assert.Contains(Notices(lost), n => n.Contains("Pose Playback cancelled") && n.Contains("1 of 3"));
        Assert.Empty(stale);
        Assert.Equal(["avatarpose a1 " + Payload(1000, 0)], Sent(again));
    }
}
