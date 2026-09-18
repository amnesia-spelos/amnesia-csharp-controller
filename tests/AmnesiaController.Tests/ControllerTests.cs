using AmnesiaController.Core;

namespace AmnesiaController.Tests;

public class ControllerTests
{
    private static readonly DateTime T0 = new(2026, 9, 17, 14, 30, 5, 123);

    private static Controller ConnectedController()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.ConnectionEstablished(T0);
        return controller;
    }

    [Fact]
    public void Entered_line_is_sent_verbatim_and_shown_as_sent()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("chat:Žofie:ahoj 👋 ", T0);

        Assert.Equal(
            [
                new SendWireLine("chat:Žofie:ahoj 👋 "),
                new ShowLine("14:30:05.123", Direction.Sent, null, "chat:Žofie:ahoj 👋 "),
            ],
            effects);
    }

    [Fact]
    public void Entered_line_is_rejected_with_a_notice_while_never_connected()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));

        var effects = controller.LineEntered("ping", T0);

        var notice = Assert.IsType<ShowLine>(Assert.Single(effects));
        Assert.Equal(Direction.Notice, notice.Direction);
        Assert.Contains("not sent", notice.Text);
    }

    [Fact]
    public void Entered_line_is_rejected_after_connection_is_lost()
    {
        var controller = ConnectedController();
        controller.ConnectionLost(T0);

        var effects = controller.LineEntered("ping", T0);

        Assert.DoesNotContain(effects, e => e is SendWireLine);
    }

    [Fact]
    public void Connection_changes_are_announced_as_notices()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));

        var established = Assert.IsType<ShowLine>(Assert.Single(controller.ConnectionEstablished(T0)));
        var lost = Assert.IsType<ShowLine>(Assert.Single(controller.ConnectionLost(T0)));

        Assert.Equal(Direction.Notice, established.Direction);
        Assert.Contains("Connected", established.Text);
        Assert.Equal(Direction.Notice, lost.Direction);
        Assert.Contains("Disconnected", lost.Text);
    }

    [Theory]
    [InlineData("RESPONSE:ping:pong", LineCategory.Response)]
    [InlineData("EVENT:MapChanged:maps/main/level02.map", LineCategory.Event)]
    [InlineData("WARNING:Unknown command", LineCategory.Warning)]
    [InlineData("SCRIPT_CALL:OnCollide(\"Player\", \"Door\", 1)", LineCategory.ScriptCall)]
    [InlineData("Hello, from Amnesia: The Dark Descent!", LineCategory.Greeting)]
    [InlineData("STATE localpose 123456 3 1.2500 -2.5000 3.7500 90.0000 -45.0000 1 maps/a.map", LineCategory.State)]
    [InlineData("STATE not-a-valid-pose", LineCategory.State)]
    [InlineData("STATElocalpose", LineCategory.Uncategorised)]
    [InlineData("custom_stories/a\tMy Story", LineCategory.Uncategorised)]
    [InlineData("response:lowercase is not a marker", LineCategory.Uncategorised)]
    public void Received_wire_line_is_shown_as_is_with_its_line_category(string line, LineCategory category)
    {
        var controller = ConnectedController();

        var effects = controller.WireLineReceived(line, T0);

        Assert.Equal([new ShowLine("14:30:05.123", Direction.Received, category, line)], effects);
    }

    [Fact]
    public void Greeting_is_shown_again_after_reconnecting()
    {
        var controller = ConnectedController();
        controller.WireLineReceived("Hello, from Amnesia: The Dark Descent!", T0);
        controller.ConnectionLost(T0);
        controller.ConnectionEstablished(T0);

        var effects = controller.WireLineReceived("Hello, from Amnesia: The Dark Descent!", T0);

        Assert.Equal(LineCategory.Greeting, Assert.IsType<ShowLine>(Assert.Single(effects)).Category);
    }

    [Fact]
    public void Double_slash_sends_a_wire_line_starting_with_a_single_slash()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("//quit", T0);

        Assert.Equal(
            [
                new SendWireLine("/quit"),
                new ShowLine("14:30:05.123", Direction.Sent, null, "/quit"),
            ],
            effects);
    }

    [Fact]
    public void Quit_directive_exits_successfully_without_sending()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/quit", T0);

        Assert.Equal([new ExitController(0)], effects);
    }

    [Fact]
    public void Clear_directive_clears_the_screen_without_sending()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/clear", T0);

        Assert.Equal([new ClearScreen()], effects);
    }

    [Fact]
    public void Reconnect_directive_requests_a_reconnect_with_a_notice()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/reconnect", T0);

        Assert.Contains(new RequestReconnect(), effects);
        Assert.Contains(effects, e => e is ShowLine { Direction: Direction.Notice });
        Assert.DoesNotContain(effects, e => e is SendWireLine);
    }

    [Fact]
    public void Help_directive_lists_directives_and_the_escape_as_notices()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/help", T0);

        Assert.All(effects, e => Assert.Equal(Direction.Notice, Assert.IsType<ShowLine>(e).Direction));
        var text = string.Join("\n", effects.Cast<ShowLine>().Select(s => s.Text));
        foreach (var expected in new[]
                 {
                     "/help", "/quit", "/reconnect", "/clear", "/mute", "/unmute", "//", "state",
                     "/pose-record <seconds>", "/pose-record-stop", "/pose-record-cancel", "/pose-play [avatar-id]", "/pose-play-stop", "/pose-status",
                 })
            Assert.Contains(expected, text);
    }

    [Fact]
    public void Unknown_directive_is_reported_and_not_sent()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/qiut", T0);

        var notice = Assert.IsType<ShowLine>(Assert.Single(effects));
        Assert.Equal(Direction.Notice, notice.Direction);
        Assert.Contains("/qiut", notice.Text);
    }

    [Fact]
    public void Directives_work_while_disconnected()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));

        var effects = controller.LineEntered("/quit", T0);

        Assert.Equal([new ExitController(0)], effects);
    }

    [Fact]
    public void Muted_line_category_is_hidden_and_other_categories_still_show()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute script_call", T0);

        var hidden = controller.WireLineReceived("SCRIPT_CALL:OnStart()", T0);
        var shown = controller.WireLineReceived("RESPONSE:ping:pong", T0);

        Assert.Empty(hidden);
        Assert.Single(shown);
    }

    [Fact]
    public void Unmute_reports_how_many_lines_were_hidden_and_shows_the_category_again()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute SCRIPT_CALL", T0);
        controller.WireLineReceived("SCRIPT_CALL:OnStart()", T0);
        controller.WireLineReceived("SCRIPT_CALL:OnEnter()", T0);
        controller.WireLineReceived("SCRIPT_CALL:OnLeave()", T0);

        var unmuted = controller.LineEntered("/unmute script_call", T0);
        var shown = controller.WireLineReceived("SCRIPT_CALL:OnStart()", T0);

        var notice = Assert.IsType<ShowLine>(Assert.Single(unmuted));
        Assert.Equal(Direction.Notice, notice.Direction);
        Assert.Contains("3", notice.Text);
        Assert.Single(shown);
    }

    [Fact]
    public void Mute_and_unmute_do_not_send_anything_to_the_game()
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered("/mute event", T0).Concat(controller.LineEntered("/unmute event", T0));

        Assert.All(effects, e => Assert.Equal(Direction.Notice, Assert.IsType<ShowLine>(e).Direction));
    }

    [Fact]
    public void State_line_category_can_be_muted_without_hiding_uncategorised_lines()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute state", T0);

        var hidden = controller.WireLineReceived("STATE localpose 1 0 0.0000 0.0000 0.0000 0.0000 0.0000 0 m.map", T0);
        var shown = controller.WireLineReceived("RESPONSE localpose ok subscribe 60", T0);

        Assert.Empty(hidden);
        Assert.Single(shown);
    }

    [Theory]
    [InlineData("/mute")]
    [InlineData("/mute chat")]
    [InlineData("/unmute nonsense")]
    public void Mute_with_a_missing_or_unknown_category_is_reported(string directive)
    {
        var controller = ConnectedController();

        var effects = controller.LineEntered(directive, T0);

        var notice = Assert.IsType<ShowLine>(Assert.Single(effects));
        Assert.Contains("category", notice.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void After_input_ends_the_controller_exits_once_the_game_has_been_quiet_for_the_linger_period()
    {
        var controller = ConnectedController();
        controller.InputEnded(T0);

        var early = controller.TimePassed(T0.AddMilliseconds(1999));
        var due = controller.TimePassed(T0.AddMilliseconds(2000));

        Assert.Empty(early);
        Assert.Equal([new ExitController(0)], due);
    }

    [Fact]
    public void Received_wire_line_resets_the_linger_quiet_period()
    {
        var controller = ConnectedController();
        controller.InputEnded(T0);
        controller.WireLineReceived("EVENT:CustomStoryStarted", T0.AddMilliseconds(1500));

        var early = controller.TimePassed(T0.AddMilliseconds(3000));
        var due = controller.TimePassed(T0.AddMilliseconds(3500));

        Assert.DoesNotContain(early, e => e is ExitController);
        Assert.Equal([new ExitController(0)], due);
    }

    [Fact]
    public void Linger_period_is_configurable()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(250));
        controller.ConnectionEstablished(T0);
        controller.InputEnded(T0);

        Assert.Equal([new ExitController(0)], controller.TimePassed(T0.AddMilliseconds(250)));
    }

    [Fact]
    public void Muted_wire_line_still_resets_the_linger_quiet_period()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute script_call", T0);
        controller.InputEnded(T0);
        controller.WireLineReceived("SCRIPT_CALL:OnUpdate()", T0.AddMilliseconds(1500));

        Assert.Empty(controller.TimePassed(T0.AddMilliseconds(3000)));
    }

    [Fact]
    public void Muted_state_update_still_resets_the_linger_quiet_period()
    {
        var controller = ConnectedController();
        controller.LineEntered("/mute state", T0);
        controller.InputEnded(T0);
        controller.WireLineReceived("STATE localpose 1 0 0.0000 0.0000 0.0000 0.0000 0.0000 0 m.map", T0.AddMilliseconds(1500));

        Assert.Empty(controller.TimePassed(T0.AddMilliseconds(3000)));
    }

    [Fact]
    public void Piped_run_that_never_connected_exits_with_a_non_zero_code()
    {
        var controller = new Controller(TimeSpan.FromMilliseconds(2000));
        controller.LineEntered("ping", T0);
        controller.InputEnded(T0);

        var effects = controller.TimePassed(T0.AddMilliseconds(2000));

        var exit = Assert.IsType<ExitController>(Assert.Single(effects));
        Assert.NotEqual(0, exit.ExitCode);
    }

    [Fact]
    public void Piped_run_that_connected_and_later_lost_the_connection_exits_successfully()
    {
        var controller = ConnectedController();
        controller.ConnectionLost(T0);
        controller.InputEnded(T0);

        Assert.Equal([new ExitController(0)], controller.TimePassed(T0.AddMilliseconds(2000)));
    }

    [Fact]
    public void Controller_does_not_exit_on_its_own_while_input_has_not_ended()
    {
        var controller = ConnectedController();

        Assert.Empty(controller.TimePassed(T0.AddHours(1)));
    }
}
