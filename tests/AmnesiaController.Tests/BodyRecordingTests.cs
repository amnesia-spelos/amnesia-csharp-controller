using AmnesiaController.Core;

namespace AmnesiaController.Tests;

public class BodyRecordingTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private const string Entry = "12 1 1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -9.8000 0.0000 90.0000 0.0000";

    private static Controller ConnectedController()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        return controller;
    }

    private static string Bodies(ulong timeMs, string map = "maps/cellar.map") =>
        $"STATE reportedbodies {timeMs} 1 {Entry} {map}";

    private static IEnumerable<string> Notices(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<ShowLine>().Where(s => s.Direction == Direction.Notice).Select(s => s.Text);

    [Fact]
    public void Recording_starts_on_the_first_valid_reported_bodies_state_update_which_is_still_shown()
    {
        var controller = ConnectedController();
        var armed = controller.LineEntered("/bodies-record 5", T0);

        var effects = controller.WireLineReceived(Bodies(1000), T0);

        Assert.Contains("Body Recording armed", Assert.Single(Notices(armed)));
        Assert.Equal(new ShowLine("14:30:05.123", Direction.Received, LineCategory.State, Bodies(1000)), effects[0]);
        Assert.Contains(Notices(effects), n => n.Contains("Body Recording started"));
    }

    [Fact]
    public void Recording_duration_is_measured_in_game_time_and_includes_the_first_sample_at_or_beyond_it()
    {
        var controller = ConnectedController();
        controller.LineEntered("/bodies-record 5", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(3000), T0.AddMinutes(10));

        var before = controller.WireLineReceived(Bodies(5999), T0.AddMinutes(20));
        var boundary = controller.WireLineReceived(Bodies(6016), T0.AddMinutes(20));
        var after = controller.WireLineReceived(Bodies(7000), T0.AddMinutes(20));

        Assert.Empty(Notices(before));
        var completed = Assert.Single(Notices(boundary));
        Assert.Contains("Body Recording complete", completed);
        Assert.Contains("4 sample(s), 5.016 s", completed);
        Assert.Empty(Notices(after));
        Assert.Contains("Body Recording Buffer: 4 sample(s), 5.016 s", Status(controller));
    }

    [Theory]
    [InlineData("/bodies-record")]
    [InlineData("/bodies-record 0")]
    [InlineData("/bodies-record .5")]
    [InlineData("/bodies-record 3600.001")]
    public void Invalid_recording_duration_is_rejected_and_nothing_is_armed(string directive)
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered(directive, T0);
        var afterSample = controller.WireLineReceived(Bodies(1000), T0);

        Assert.Contains("duration", Assert.Single(Notices(effects)), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Notices(afterSample));
    }

    [Fact]
    public void Recording_cannot_start_while_disconnected()
    {
        var controller = ConnectedController();
        controller.ConnectionLost(T0);

        var effects = controller.LineEntered("/bodies-record 5", T0);

        Assert.Contains("Not connected; Body Recording not started", Assert.Single(Notices(effects)));
    }

    public static TheoryData<string> MalformedLines() => new()
    {
        $"STATE reportedbodies 1000 1 {Entry}",
        $"STATE reportedbodies 1000 1 {Entry} ",
        "STATE reportedbodies 1000 0",
        "STATE reportedbodies 1000 0 ",
        $"STATE reportedbodies 1000  1 {Entry} m.map",
        $"STATE reportedbodies -1000 1 {Entry} m.map",
        $"STATE reportedbodies +1000 1 {Entry} m.map",
        $"STATE reportedbodies 18446744073709551616 1 {Entry} m.map",
        $"STATE reportedbodies 1000 -1 {Entry} m.map",
        $"STATE reportedbodies 1000 +1 {Entry} m.map",
        $"STATE reportedbodies 1000 x {Entry} m.map",
        $"STATE reportedbodies 1000 33 {string.Join(' ', Enumerable.Repeat(Entry, 33))} m.map",
        $"STATE reportedbodies 1000 2 {Entry} m.map",
        $"STATE reportedbodies 1000 2 {Entry} a b c d e f g h i j k l m n o p",
        $"STATE reportedbodies 1000 1 {Entry[..Entry.LastIndexOf(' ')]} m.map",
        $"STATE reportedbodies 1000 1 +12 1 {Entry[5..]} m.map",
        $"STATE reportedbodies 1000 1 2147483648 1 {Entry[5..]} m.map",
        $"STATE reportedbodies 1000 1 12 -2147483649 {Entry[5..]} m.map",
        $"STATE reportedbodies 1000 1 12 1.0 {Entry[5..]} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", "1,2500")} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", "1e3")} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", ".5")} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", "+1.2500")} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", "nan")} m.map",
        $"STATE reportedbodies 1000 1 {Entry.Replace("1.2500", "1234567890.123456")} m.map",
        $"STATE reportedbodiesx 1000 1 {Entry} m.map",
        $"STATE localpose 1000 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 m.map",
    };

    [Theory]
    [MemberData(nameof(MalformedLines))]
    public void Malformed_or_other_state_update_is_shown_but_not_recorded(string malformed)
    {
        var controller = ConnectedController();
        controller.LineEntered("/bodies-record 5", T0);

        var shown = controller.WireLineReceived(malformed, T0);
        var firstValid = controller.WireLineReceived(Bodies(1000), T0);

        Assert.Equal([new ShowLine("14:30:05.123", Direction.Received, LineCategory.State, malformed)], shown);
        Assert.Contains(Notices(firstValid), n => n.Contains("started"));
    }

    public static TheoryData<string> WellFormedLines() => new()
    {
        "STATE reportedbodies 1000 0 m.map",
        $"STATE reportedbodies 18446744073709551615 2 -2147483648 2147483647 {Entry[5..]} 007 -0 {Entry[5..]} custom_stories/My Story: Part 2/maps/cellar one.map",
        $"STATE reportedbodies 1000 32 {string.Join(' ', Enumerable.Repeat(Entry, 32))} m.map",
        $"STATE reportedbodies 1000 1 12 1 0 -1 123456789012345 -0.0001 12345678901234.5 0 0 0 0 0 0 0 0 m.map",
    };

    [Theory]
    [MemberData(nameof(WellFormedLines))]
    public void Well_formed_reported_bodies_state_update_starts_the_recording(string valid)
    {
        var controller = ConnectedController();
        controller.LineEntered("/bodies-record 5", T0);

        var effects = controller.WireLineReceived(valid, T0);

        Assert.Contains(Notices(effects), n => n.Contains("started"));
    }

    [Fact]
    public void Status_reports_idle_without_a_buffer_and_an_armed_recording_once_armed()
    {
        var controller = ConnectedController();

        var idle = Status(controller);
        controller.LineEntered("/bodies-record 5", T0);
        var armed = Status(controller);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(2500), T0);
        var started = Status(controller);

        Assert.Contains("idle", idle);
        Assert.Contains("No Body Recording Buffer", idle);
        Assert.Contains("Body Recording armed", armed);
        Assert.Contains("waiting for the first Reported Bodies State Update", armed);
        Assert.Contains("2 sample(s), 1.500 of 5.000 s", started);
    }

    [Fact]
    public void Stopping_after_the_first_sample_completes_the_recording_early()
    {
        var controller = ControllerWithBuffer(2);
        controller.LineEntered("/bodies-record 60", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(1500), T0);

        var stopped = Assert.Single(Notices(controller.LineEntered("/bodies-record-stop", T0)));

        Assert.Contains("Body Recording stopped early: 2 sample(s), 0.500 s", stopped);
        Assert.Contains("Body Recording Buffer: 2 sample(s), 0.500 s", Status(controller));
    }

    [Fact]
    public void Stopping_before_the_first_sample_cancels_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(3);
        controller.LineEntered("/bodies-record 60", T0);

        var stopped = Assert.Single(Notices(controller.LineEntered("/bodies-record-stop", T0)));

        Assert.Contains("Body Recording cancelled", stopped);
        Assert.Contains("idle", Status(controller));
        Assert.Contains("Body Recording Buffer: 3 sample(s)", Status(controller));
    }

    [Fact]
    public void Cancelling_abandons_the_working_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(3);
        controller.LineEntered("/bodies-record 60", T0);
        controller.WireLineReceived(Bodies(1000), T0);

        var cancelled = Assert.Single(Notices(controller.LineEntered("/bodies-record-cancel", T0)));

        Assert.Contains("Body Recording cancelled", cancelled);
        Assert.Contains("Body Recording Buffer: 3 sample(s)", Status(controller));
    }

    [Theory]
    [InlineData("/bodies-record-stop")]
    [InlineData("/bodies-record-cancel")]
    public void Stopping_or_cancelling_without_an_active_recording_is_reported(string directive)
    {
        var controller = ConnectedController();

        Assert.Contains("No Body Recording is active", Assert.Single(Notices(controller.LineEntered(directive, T0))));
    }

    [Fact]
    public void Backward_game_time_cancels_the_working_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(3);
        controller.LineEntered("/bodies-record 60", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(2000), T0);

        var effects = controller.WireLineReceived(Bodies(1999), T0);

        Assert.Equal(LineCategory.State, Assert.IsType<ShowLine>(effects[0]).Category);
        var notice = Assert.Single(Notices(effects));
        Assert.Contains("Body Recording cancelled", notice);
        Assert.Contains("backwards", notice);
        Assert.Contains("Body Recording Buffer: 3 sample(s)", Status(controller));
    }

    [Fact]
    public void Disconnecting_cancels_the_recording_and_keeps_the_previous_buffer()
    {
        var controller = ControllerWithBuffer(3);
        controller.LineEntered("/bodies-record 60", T0);
        controller.WireLineReceived(Bodies(1000), T0);

        var effects = controller.ConnectionLost(T0);
        controller.ConnectionEstablished(T0);
        var afterReconnect = controller.WireLineReceived(Bodies(2000), T0);

        Assert.Contains(Notices(effects), n => n.Contains("Body Recording cancelled by disconnection"));
        Assert.Empty(Notices(afterReconnect));
        Assert.Contains("Body Recording Buffer: 3 sample(s)", Status(controller));
    }

    [Fact]
    public void Muted_state_updates_are_still_recorded()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute state", T0);
        controller.LineEntered("/bodies-record 1", T0);

        var started = controller.WireLineReceived(Bodies(1000), T0);
        var completed = controller.WireLineReceived(Bodies(2000), T0);

        Assert.Contains("started", Assert.Single(Notices(started)));
        Assert.Contains("2 sample(s)", Assert.Single(Notices(completed)));
        Assert.DoesNotContain(started.Concat(completed), e => e is ShowLine { Direction: Direction.Received });
    }

    /// <summary>A connected Controller whose Body Recording Buffer holds the given number of samples.</summary>
    private static Controller ControllerWithBuffer(int samples)
    {
        var controller = ConnectedController();
        controller.LineEntered("/bodies-record 1", T0);
        for (var i = 0; i < samples - 1; i++)
            controller.WireLineReceived(Bodies(10 + (ulong)i), T0);
        controller.WireLineReceived(Bodies(5000), T0);
        return controller;
    }

    private static string Status(Controller controller) =>
        string.Join("\n", Notices(controller.LineEntered("/bodies-status", T0)));
}
