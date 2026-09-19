# amnesia-csharp-controller

An **internal development tool** for manually exercising the Game Interaction Protocol of
[amnesia-tdd-tcp](https://github.com/amnesia-spelos/amnesia-tdd-tcp) against a running `Amnesia.exe`.
It is never shipped and is not a general consumer of the protocol: every line you type is sent verbatim as a Wire Line,
and every received Wire Line is shown with a timestamp and a colour by Line Category.
The only protocol automation is the explicit [Pose Recording and Pose Playback](#pose-recording-and-pose-playback) Directives.

## Running

Requires the .NET 10 SDK.

```sh
dotnet run --project src/AmnesiaController
dotnet run --project src/AmnesiaController -- --host 192.168.1.20 --port 5150
```

| Option          | Default     | Meaning                                                                 |
| --------------- | ----------- | ----------------------------------------------------------------------- |
| `--host <host>` | `127.0.0.1` | Game host                                                               |
| `--port <port>` | `5150`      | Game port                                                               |
| `--linger <ms>` | `2000`      | After piped input ends, exit once the game has been quiet for this long |

The Controller retries every second until the game is listening, and again after a disconnection.
Lines typed while disconnected are rejected, not queued.

Output lines are marked `>` (sent), `<` (received) and `*` (local notice).

## Directives

Directives start with `/` and are never sent to the game. Type `//` to send a Wire Line that starts with `/`.

| Directive             | Effect                                                                  |
| --------------------- | ----------------------------------------------------------------------- |
| `/help`               | List Directives                                                         |
| `/quit`               | Exit                                                                    |
| `/reconnect`          | Drop the connection and start a fresh Session                           |
| `/clear`              | Clear the screen                                                        |
| `/mute <category>`    | Hide received lines of a Line Category (they are still counted)         |
| `/unmute <category>`  | Show them again and report how many were hidden                         |
| `/pose-record <seconds>` | Arm a Pose Recording of up to 3600 s of game time (e.g. `5` or `0.5`) |
| `/pose-record-stop`   | Finish the recording early; before the first sample it cancels instead  |
| `/pose-record-cancel` | Abandon the recording and keep the previous Pose Recording Buffer       |
| `/pose-play [avatar-id]` | Play the Pose Recording Buffer into an Avatar (default `a1`)         |
| `/pose-play-stop`     | Stop playback and keep the buffer                                       |
| `/pose-status`        | Show the active pose operation and the buffer                          |

Categories: `response`, `event`, `state`, `warning`, `script_call`, `greeting`, `uncategorised`.
Muting is presentation only; it is not an Event Subscription.

## Pose Recording and Pose Playback

Record your own movement from `STATE localpose` State Updates, then play it back into an Avatar
as paced `avatarpose` Commands. The Controller never negotiates, subscribes or creates the Avatar for you:

```text
protocol 2 avatars localpose
avatarcreate a1
localpose subscribe 60
/mute state
/pose-record 5
```

Recording is armed until the first valid Local Pose State Update arrives; that sample starts the clock.
Duration is measured in the game time embedded in each State Update, so pauses and menus do not use it up,
and the first sample at or beyond the duration is included. Muted State Updates are still recorded.
Malformed State Updates are shown but not recorded, and a State Update whose game time goes backwards cancels the recording.
Only one Pose Recording Buffer is kept, in memory; it is replaced only when a recording completes or is stopped early.

```text
localpose unsubscribe
/pose-play
/pose-status
```

Playback sends every recorded pose in order as `avatarpose <avatar-id> <original State Update fields>`,
unchanged, each due its recorded game time after the first, so timer granularity does not add up.
Poses with the same game time are sent together, and a send more than 100 ms late stretches the rest of the playback
instead of bursting. Each generated Command is shown as sent;
a successful `avatarpose` has no Response.

Recording and playback cannot run at the same time, and neither starts while disconnected.
A disconnection cancels the active one but keeps the buffer; in the new Session, negotiate and create the Avatar again before playing.

## Replaying Wire Lines

Pipe a file of lines (Directives included) to replay a setup:

```sh
dotnet run --project src/AmnesiaController < replay.txt
```

The Controller exits once the game has been quiet for the linger period after input ends,
with a non-zero exit code if it never connected. It does not exit while a Pose Recording or Pose Playback is active.

## Tests

```sh
dotnet test
```

Only the I/O-free Controller core is tested; the TCP and terminal adapters are verified manually against the game.
