using AmnesiaController.Core;

namespace AmnesiaController.Tests;

public class PoseRecordingTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private static Controller ConnectedController()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        return controller;
    }

    private static string Pose(ulong timeMs, string map = "maps/cellar.map") =>
        $"STATE localpose {timeMs} 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 {map}";

    private static IEnumerable<string> Notices(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<ShowLine>().Where(s => s.Direction == Direction.Notice).Select(s => s.Text);

    /// <summary>A connected Controller whose Pose Recording Buffer holds the given number of samples.</summary>
    private static Controller ControllerWithBuffer(int samples)
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 1", T0);
        for (var i = 0; i < samples - 1; i++)
            controller.WireLineReceived(Pose(10 + (ulong)i), T0);
        controller.WireLineReceived(Pose(5000), T0);
        return controller;
    }

    private static string Status(Controller controller) =>
        string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));

    [Fact]
    public void Arming_a_recording_sends_nothing_and_announces_it_is_waiting_for_the_first_sample()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/pose-record 5", T0);

        var notice = Assert.IsType<ShowLine>(Assert.Single(effects));
        Assert.Equal(Direction.Notice, notice.Direction);
        Assert.Contains("armed", notice.Text);
    }

    [Fact]
    public void Recording_starts_on_the_first_valid_local_pose_state_update_which_is_still_shown()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 5", T0);

        var effects = controller.WireLineReceived(Pose(1000), T0);

        Assert.Equal(new ShowLine("14:30:05.123", Direction.Received, LineCategory.State, Pose(1000)), effects[0]);
        Assert.Contains(Notices(effects), n => n.Contains("started"));
    }

    [Fact]
    public void Recording_duration_is_measured_in_game_time_and_includes_the_first_sample_at_or_beyond_it()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 5", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(3000), T0.AddMinutes(10));

        var before = controller.WireLineReceived(Pose(5999), T0.AddMinutes(20));
        var boundary = controller.WireLineReceived(Pose(6016), T0.AddMinutes(20));
        var after = controller.WireLineReceived(Pose(7000), T0.AddMinutes(20));

        Assert.Empty(Notices(before));
        var completed = Assert.Single(Notices(boundary));
        Assert.Contains("complete", completed);
        Assert.Contains("4 sample(s)", completed);
        Assert.Contains("5.016 s", completed);
        Assert.Empty(Notices(after));
    }

    [Theory]
    [InlineData("/pose-record")]
    [InlineData("/pose-record 0")]
    [InlineData("/pose-record 0.0")]
    [InlineData("/pose-record -5")]
    [InlineData("/pose-record 0,5")]
    [InlineData("/pose-record 5s")]
    [InlineData("/pose-record 5 6")]
    [InlineData("/pose-record +5")]
    [InlineData("/pose-record 1e2")]
    [InlineData("/pose-record .5")]
    [InlineData("/pose-record 5.")]
    [InlineData("/pose-record 3600.001")]
    [InlineData("/pose-record 99999999999999999999999999999999")]
    public void Invalid_recording_duration_is_rejected_and_nothing_is_armed(string directive)
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered(directive, T0);
        var afterSample = controller.WireLineReceived(Pose(1000), T0);

        Assert.Contains("duration", Assert.Single(Notices(effects)), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Notices(afterSample));
    }

    [Theory]
    [InlineData("/pose-record 3600")]
    [InlineData("/pose-record 0.001")]
    [InlineData("/pose-record 12.5")]
    public void Positive_integer_or_fractional_durations_up_to_one_hour_are_accepted(string directive)
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered(directive, T0);

        Assert.Contains("armed", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Recording_cannot_start_while_disconnected()
    {
        var controller = ConnectedController();
        controller.ConnectionLost(T0);

        var effects = controller.LineEntered("/pose-record 5", T0);
        controller.ConnectionEstablished(T0);
        var afterSample = controller.WireLineReceived(Pose(1000), T0);

        Assert.Contains("Not connected", Assert.Single(Notices(effects)));
        Assert.Empty(Notices(afterSample));
    }

    [Theory]
    [InlineData("STATE localpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1")]
    [InlineData("STATE localpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 ")]
    [InlineData("STATE localpose 1000  3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose -1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose +1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 18446744073709551616 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 4294967296 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 -3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 1,2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 .5 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 1. -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 +1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 1e3 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 nan -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 1234567890.123456 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE localpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 2 m.map")]
    [InlineData("STATE localpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 m.map")]
    [InlineData("STATE localposex 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    [InlineData("STATE otherpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map")]
    public void Malformed_local_pose_state_update_is_shown_but_not_recorded(string malformed)
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 5", T0);

        var shown = controller.WireLineReceived(malformed, T0);
        var firstValid = controller.WireLineReceived(Pose(1000), T0);

        Assert.Equal([new ShowLine("14:30:05.123", Direction.Received, LineCategory.State, malformed)], shown);
        Assert.Contains(Notices(firstValid), n => n.Contains("started"));
    }

    [Theory]
    [InlineData("STATE localpose 0 0 0 -1 123456789012345 -0.0001 12345678901234.5 0 m.map")]
    [InlineData("STATE localpose 18446744073709551615 4294967295 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 custom_stories/My Story: Part 2/maps/cellar one.map")]
    public void Well_formed_local_pose_state_update_starts_the_recording(string valid)
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 5", T0);

        var effects = controller.WireLineReceived(valid, T0);

        Assert.Contains(Notices(effects), n => n.Contains("started"));
    }

    [Fact]
    public void Status_reports_idle_without_a_buffer()
    {
        var controller = ConnectedController();

        var status = string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));

        Assert.Contains("idle", status);
        Assert.Contains("No Pose Recording Buffer", status);
    }

    [Fact]
    public void Status_distinguishes_an_armed_recording_from_a_started_one()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 5", T0);

        var armed = string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(2500), T0);
        var started = string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));

        Assert.Contains("armed", armed);
        Assert.Contains("waiting for the first", armed);
        Assert.Contains("2 sample(s)", started);
        Assert.Contains("1.500 of 5.000 s", started);
    }

    [Fact]
    public void Status_reports_the_completed_buffer_sample_count_and_game_time_duration()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 1", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(1500), T0);
        controller.WireLineReceived(Pose(2016), T0);

        var status = string.Join("\n", Notices(controller.LineEntered("/pose-status", T0)));

        Assert.Contains("idle", status);
        Assert.Contains("Pose Recording Buffer: 3 sample(s), 1.016 s", status);
    }

    [Fact]
    public void Stopping_after_the_first_sample_completes_the_recording_early()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 60", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(1250), T0);
        controller.WireLineReceived(Pose(1500), T0);

        var stopped = Assert.Single(Notices(controller.LineEntered("/pose-record-stop", T0)));

        Assert.Contains("stopped early", stopped);
        Assert.Contains("3 sample(s), 0.500 s", stopped);
        Assert.Contains("idle", Status(controller));
        Assert.Contains("Pose Recording Buffer: 3 sample(s), 0.500 s", Status(controller));
    }

    [Fact]
    public void Stopping_before_the_first_sample_cancels_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 60", T0);

        var stopped = Assert.Single(Notices(controller.LineEntered("/pose-record-stop", T0)));

        Assert.Contains("cancelled", stopped);
        Assert.Contains("idle", Status(controller));
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", Status(controller));
    }

    [Fact]
    public void A_one_sample_recording_stopped_early_is_a_usable_buffer()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 60", T0);
        controller.WireLineReceived(Pose(1000), T0);

        controller.LineEntered("/pose-record-stop", T0);

        Assert.Contains("Pose Recording Buffer: 1 sample(s), 0.000 s", Status(controller));
    }

    [Fact]
    public void Cancelling_abandons_the_working_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 60", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(2000), T0);

        var cancelled = Assert.Single(Notices(controller.LineEntered("/pose-record-cancel", T0)));

        Assert.Contains("cancelled", cancelled);
        Assert.Contains("idle", Status(controller));
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", Status(controller));
    }

    [Theory]
    [InlineData("/pose-record-stop")]
    [InlineData("/pose-record-cancel")]
    public void Stopping_or_cancelling_without_an_active_recording_is_reported(string directive)
    {
        var controller = ConnectedController();

        var notice = Assert.Single(Notices(controller.LineEntered(directive, T0)));

        Assert.Contains("No Pose Recording", notice);
    }

    [Fact]
    public void Completed_recording_replaces_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 1", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(1500), T0);

        Assert.Contains("Pose Recording Buffer: 2 sample(s), 4.990 s", Status(controller));

        controller.WireLineReceived(Pose(1500), T0);
        controller.WireLineReceived(Pose(2000), T0);

        Assert.Contains("Pose Recording Buffer: 4 sample(s), 1.000 s", Status(controller));
    }

    [Fact]
    public void Duplicate_timestamps_are_recorded()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 1", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(1000), T0);

        var completed = controller.WireLineReceived(Pose(2000), T0);

        Assert.Contains("4 sample(s), 1.000 s", Assert.Single(Notices(completed)));
    }

    [Fact]
    public void Backward_game_time_cancels_the_working_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 60", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(2000), T0);

        var effects = controller.WireLineReceived(Pose(1999), T0);

        Assert.Equal(LineCategory.State, Assert.IsType<ShowLine>(effects[0]).Category);
        var notice = Assert.Single(Notices(effects));
        Assert.Contains("cancelled", notice);
        Assert.Contains("backwards", notice);
        Assert.Contains("idle", Status(controller));
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", Status(controller));
    }

    [Fact]
    public void Starting_a_recording_while_one_is_active_is_rejected_without_disturbing_it()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 1", T0);
        controller.WireLineReceived(Pose(1000), T0);

        var rejected = Assert.Single(Notices(controller.LineEntered("/pose-record 60", T0)));
        var completed = controller.WireLineReceived(Pose(2000), T0);

        Assert.Contains("already", rejected);
        Assert.Contains("2 sample(s), 1.000 s", Assert.Single(Notices(completed)));
    }

    [Fact]
    public void Disconnecting_cancels_the_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/pose-record 60", T0);
        controller.WireLineReceived(Pose(1000), T0);

        var effects = controller.ConnectionLost(T0);
        controller.ConnectionEstablished(T0);
        var afterReconnect = controller.WireLineReceived(Pose(2000), T0);

        Assert.Contains(Notices(effects), n => n.Contains("Disconnected"));
        Assert.Contains(Notices(effects), n => n.Contains("Pose Recording cancelled"));
        Assert.Empty(Notices(afterReconnect));
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", Status(controller));
    }

    [Fact]
    public void Muted_state_updates_are_still_recorded()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute state", T0);
        controller.LineEntered("/pose-record 1", T0);

        var started = controller.WireLineReceived(Pose(1000), T0);
        var completed = controller.WireLineReceived(Pose(2000), T0);

        Assert.Contains("started", Assert.Single(Notices(started)));
        Assert.Contains("2 sample(s)", Assert.Single(Notices(completed)));
        Assert.DoesNotContain(started.Concat(completed), e => e is ShowLine { Direction: Direction.Received });
    }

    [Fact]
    public void Sample_exactly_at_the_requested_duration_completes_the_recording()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 0.5", T0);
        controller.WireLineReceived(Pose(1000), T0);

        var boundary = controller.WireLineReceived(Pose(1500), T0);

        Assert.Contains("2 sample(s)", Assert.Single(Notices(boundary)));
    }
}
