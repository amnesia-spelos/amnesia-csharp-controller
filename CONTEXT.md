# Protocol Development Tooling

This context covers a development tool for manually exercising the Game Interaction Protocol against a running game. Terms defined by `../amnesia-tdd-tcp/CONTEXT.md` (Game Interaction Protocol, Peer, Authoritative Peer, Observing Peer, Session, Command, Response, Event, and others) are used here with their upstream meaning and are not redefined.

## Language

**Controller**:
This repository's development tool: a Peer operated by a developer, which may act as an Authoritative Peer or an Observing Peer depending on what is negotiated.
_Avoid_: Client

**Wire Line**:
One newline-terminated line of Game Interaction Protocol text, exactly as sent to or received from the game.
_Avoid_: Message, packet

**Directive**:
An instruction the developer gives to the Controller itself, which is never sent to the game.
_Avoid_: Command, local command

**Line Category**:
The kind of a received Wire Line recognised from its leading marker (such as Response, Event, warning, script-call observation, or greeting), used only for presentation.
_Avoid_: Message type

**Muting**:
Hiding received Wire Lines of a Line Category from view while still counting them.
_Avoid_: Filtering, unsubscribing

## Relationships

- A **Controller** is one Peer; running several Controllers exercises multi-Peer behaviour.
- A **Controller** sends typed Wire Lines verbatim; it does not interpret the Game Interaction Protocol beyond assigning a **Line Category**.
- **Muting** is presentation only and is distinct from an Event Subscription, which the game honours.
