# amnesia-csharp-controller

An **internal development tool** for manually exercising the Game Interaction Protocol of
[amnesia-tdd-tcp](https://github.com/amnesia-spelos/amnesia-tdd-tcp) against a running `Amnesia.exe`.
It is never shipped and is not a consumer of the protocol: every line you type is sent verbatim as a Wire Line,
and every received Wire Line is shown with a timestamp and a colour by Line Category.

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

Categories: `response`, `event`, `warning`, `script_call`, `greeting`, `uncategorised`.
Muting is presentation only; it is not an Event Subscription.

## Replaying Wire Lines

Pipe a file of lines (Directives included) to replay a setup:

```sh
dotnet run --project src/AmnesiaController < replay.txt
```

The Controller exits once the game has been quiet for the linger period after input ends,
with a non-zero exit code if it never connected.

## Tests

```sh
dotnet test
```

Only the I/O-free Controller core is tested; the TCP and terminal adapters are verified manually against the game.
