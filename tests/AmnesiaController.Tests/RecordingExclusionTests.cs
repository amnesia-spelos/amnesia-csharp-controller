using AmnesiaController.Core;

namespace AmnesiaController.Tests;

/// <summary>Only one recording or playback, pose or bodies, runs at a time, and each kind keeps its own buffer.</summary>
public class RecordingExclusionTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private static string Pose(ulong timeMs) =>
        $"STATE localpose {timeMs} 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 maps/cellar.map";

    private static string Bodies(ulong timeMs) =>
        $"STATE reportedbodies {timeMs} 1 12 1 1.2500 -2.5000 3.7500 0.0000 0.0000 0.0000 1.0000 0.5000 0.0000 -9.8000 0.0000 90.0000 0.0000 maps/cellar.map";

    private static Controller ConnectedController()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        return controller;
    }

    /// <summary>A connected Controller with a two-sample Pose Recording Buffer and Body Recording Buffer.</summary>
    private static Controller ControllerWithBothBuffers()
    {
        var controller = ConnectedController();
        controller.LineEntered("/pose-record 1", T0);
        controller.WireLineReceived(Pose(1000), T0);
        controller.WireLineReceived(Pose(2000), T0);
        controller.LineEntered("/bodies-record 1", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(2000), T0);
        return controller;
    }

    private static IEnumerable<string> Notices(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<ShowLine>().Where(s => s.Direction == Direction.Notice).Select(s => s.Text);

    private static IEnumerable<string> Sent(IEnumerable<ControllerEffect> effects) =>
        effects.OfType<SendWireLine>().Select(s => s.Line);

    private static string Status(Controller controller, string directive) =>
        string.Join("\n", Notices(controller.LineEntered(directive, T0)));

    [Theory]
    [InlineData("/pose-record 5", "/bodies-record 5", "Pose Recording")]
    [InlineData("/pose-record 5", "/bodies-play", "Pose Recording")]
    [InlineData("/pose-play", "/bodies-record 5", "Pose Playback")]
    [InlineData("/pose-play", "/bodies-play", "Pose Playback")]
    [InlineData("/bodies-record 5", "/pose-record 5", "Body Recording")]
    [InlineData("/bodies-record 5", "/pose-play", "Body Recording")]
    [InlineData("/bodies-play", "/pose-record 5", "Body Playback")]
    [InlineData("/bodies-play", "/pose-play", "Body Playback")]
    public void An_operation_of_either_kind_blocks_starting_one_of_the_other(string active, string attempted, string activeName)
    {
        var controller = ControllerWithBothBuffers();
        controller.LineEntered(active, T0);

        var effects = controller.LineEntered(attempted, T0);

        Assert.Empty(Sent(effects));
        Assert.DoesNotContain(effects, e => e is ScheduleWakeup);
        Assert.Contains($"{activeName} is already active", Assert.Single(Notices(effects)));
    }

    [Fact]
    public void Body_playback_continues_undisturbed_after_a_rejected_pose_playback()
    {
        var controller = ControllerWithBothBuffers();
        var wakeup = Assert.Single(controller.LineEntered("/bodies-play", T0).OfType<ScheduleWakeup>());

        controller.LineEntered("/pose-play", T0);
        var next = controller.WakeupDue(wakeup.Token, T0.AddMilliseconds(1000));

        Assert.Equal(["entitybodies " + Bodies(2000)["STATE reportedbodies ".Length..]], Sent(next));
    }

    [Fact]
    public void A_recording_ignores_state_updates_of_the_other_kind()
    {
        var controller = ConnectedController();
        controller.LineEntered("/bodies-record 1", T0);

        var pose = controller.WireLineReceived(Pose(1000), T0);
        var status = Status(controller, "/bodies-status");

        Assert.Empty(Notices(pose));
        Assert.Contains("waiting for the first Reported Bodies State Update", status);
    }

    [Fact]
    public void Stop_directives_of_one_kind_do_not_touch_an_operation_of_the_other()
    {
        var controller = ControllerWithBothBuffers();
        controller.LineEntered("/pose-record 5", T0);
        var playStop = controller.LineEntered("/bodies-play-stop", T0);
        var recordStop = controller.LineEntered("/bodies-record-stop", T0);
        var recordCancel = controller.LineEntered("/bodies-record-cancel", T0);

        Assert.Contains("No Body Playback is active", Assert.Single(Notices(playStop)));
        Assert.Contains("No Body Recording is active", Assert.Single(Notices(recordStop)));
        Assert.Contains("No Body Recording is active", Assert.Single(Notices(recordCancel)));
        Assert.Contains("Pose Recording armed", Status(controller, "/pose-status"));
    }

    [Fact]
    public void Each_status_shows_the_active_operation_of_either_kind_and_its_own_buffer()
    {
        var controller = ControllerWithBothBuffers();
        controller.LineEntered("/bodies-record 5", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(1500), T0);

        var pose = Status(controller, "/pose-status");
        var bodies = Status(controller, "/bodies-status");

        Assert.Contains("Body Recording started", pose);
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", pose);
        Assert.DoesNotContain("Body Recording Buffer", pose);
        Assert.Contains("Body Recording started", bodies);
        Assert.Contains("Body Recording Buffer: 2 sample(s)", bodies);
    }

    [Fact]
    public void Completing_a_body_recording_keeps_the_pose_recording_buffer()
    {
        var controller = ControllerWithBothBuffers();
        controller.LineEntered("/bodies-record 1", T0);
        controller.WireLineReceived(Bodies(1000), T0);
        controller.WireLineReceived(Bodies(1500), T0);
        controller.WireLineReceived(Bodies(2000), T0);

        Assert.Contains("Body Recording Buffer: 3 sample(s)", Status(controller, "/bodies-status"));
        Assert.Contains("Pose Recording Buffer: 2 sample(s)", Status(controller, "/pose-status"));
    }
}
