# amnesia-csharp-controller

An **internal development tool** for manually exercising the Game Interaction Protocol of
[amnesia-tdd-tcp](https://github.com/amnesia-spelos/amnesia-tdd-tcp) against a running `Amnesia.exe`.
It is never shipped and is not a general consumer of the protocol: every line you type is sent verbatim as a Wire Line,
and every received Wire Line is shown with a timestamp and a colour by Line Category.
The only protocol automation is the explicit [Pose Recording and Pose Playback](#pose-recording-and-pose-playback)
and [Body Recording and Body Playback](#body-recording-and-body-playback) Directives.

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
| `/pose-status`        | Show the active operation and the Pose Recording Buffer                 |
| `/bodies-record <seconds>` | Arm a Body Recording of up to 3600 s of game time (e.g. `5` or `0.5`) |
| `/bodies-record-stop` | Finish the recording early; before the first sample it cancels instead  |
| `/bodies-record-cancel` | Abandon the recording and keep the previous Body Recording Buffer     |
| `/bodies-play`        | Play the Body Recording Buffer as `entitybodies` Commands, and its Events as `entityinteracting` and `entitybreak` |
| `/bodies-play-stop`   | Stop playback and keep the buffer                                       |
| `/bodies-status`      | Show the active operation and the Body Recording Buffer                 |

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

Only one recording or playback, pose or bodies, runs at a time, and none starts while disconnected.
A disconnection cancels the active one but keeps the buffer; in the new Session, negotiate and create the Avatar again before playing.

## Body Recording and Body Playback

Record the bodies of a prop you carry, throw, swing or break from `STATE reportedbodies` State Updates,
together with its interaction and break Events, then play them back into the same prop, driven as a Peer-Driven Entity,
as paced `entitybodies`, `entityinteracting` and `entitybreak` Commands.
This checks the driving side of the game on one PC. As with poses, the Controller never negotiates, subscribes,
or sends `entitydrive` or `entityrelease` for you:

```text
protocol 2 interactions
reportedbodies subscribe 60
/mute state
/bodies-record 5
   (grab a prop in game, carry it, throw it)
reportedbodies unsubscribe
entitydrive <entityId from the recording> <map>
/bodies-play
   (the prop plays back the carry and throw)
entityrelease <entityId> <map>
```

Recording follows the Pose Recording rules: it is armed until the first valid State Update, its duration is game time,
muted State Updates are still recorded, game time going backwards cancels it, and one Body Recording Buffer is kept in memory,
separately from the Pose Recording Buffer.
A State Update is valid when it is `STATE reportedbodies <timeMs> <count> <entry>... <map>` with a 64-bit `<timeMs>`,
a `<count>` of 0–32, exactly `<count>` entries of `<entityId> <bodyId>` and 13 numbers, and a non-empty map path to the end of the line.
Invalid ones are shown but not recorded.

`/bodies-play` takes no argument: it sends each sample as `entitybodies <original State Update fields>`, byte for byte,
so it names the entities, bodies and map it was recorded with. Pacing is the same as Pose Playback.

These Events are recorded too, even when muted, and played back as the Command that reproduces them:

| Event                                               | Command sent                              |
|-----------------------------------------------------|-------------------------------------------|
| `EVENT interactionstart <entityId> <bodyId> <map>`  | `entityinteracting <entityId> 1 <map>`    |
| `EVENT interactionend <entityId> <bodyId> <ending> <map>` | `entityinteracting <entityId> 0 <map>` |
| `EVENT reportbroke <entityId> <state> <map>`        | `entitybreak <entityId> <state> <map>`    |

An Event has no game time of its own, so it takes the game time of the State Update before it and is sent right after it.
An Event that arrives while the recording is armed, such as the `interactionstart` of a grab, is sent just before the first sample.
Events neither start nor end a recording, and a recording stopped before its first sample is cancelled even if it holds Events.
An Event after the State Update that completes the recording is not recorded, so stop the recording yourself once the prop
settles or breaks, or send `entityinteracting <entityId> 0 <map>` or `entityrelease` after playback.
A prop that breaks while held ends its interaction with `destroyed` and sends no `reportbroke`, so its playback does not break it.
`<ending>` is `released`, `thrown`, `too-far` or `destroyed`, and `<state>` is 13 numbers.
Malformed Events, and `reportcontact` and `reportsettled`, which have no Command, are shown but not recorded.

## Replaying Wire Lines

Pipe a file of lines (Directives included) to replay a setup:

```sh
dotnet run --project src/AmnesiaController < replay.txt
```

The Controller exits once the game has been quiet for the linger period after input ends,
with a non-zero exit code if it never connected. It does not exit while a recording or playback is active.

## Tests

```sh
dotnet test
```

Only the I/O-free Controller core is tested; the TCP and terminal adapters are verified manually against the game.
