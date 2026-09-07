# EveryonePicks

A ROUNDS mod that lets everyone pick their card at the same time instead of taking turns.

[Download on Thunderstore](https://thunderstore.io/c/rounds/p/RedRedRain/SimulPicks/)

The package is still called SimulPicks there because Thunderstore package names cannot be
changed after publishing. In game it is EveryonePicks.

## What it does

- Everyone picks at once, so nobody watches four other people choose before their turn
- Cards appear as each player locks one in, with that player shown above their card
- Hover a player's cards to spread them apart and read them
- A board in the bottom left shows who has picked and who you are still waiting on
- Big WAITING text while others are still choosing
- Extra picks from other mods work
- A player quitting does not break the match for everyone else

Every player in the lobby needs the mod.

## Reporting a bug

**[Open an issue](../../issues/new/choose).** Bug reports are genuinely useful, and the
templates ask for the few things that make a report actually solvable.

The single most valuable thing you can include is your log, from:

```
BepInEx\LogOutput.log
```

Two warnings about that file, both learned the hard way:

- **It is overwritten every time the game starts.** If something goes wrong, copy it out
  before relaunching, or it is gone.
- **If the problem only affected one player, that player's log is the one that matters.**
  Most of these bugs leave no trace at all in the logs of the people who were unaffected.

If more than one of you saw the problem, send both logs. Two views of the same match is
usually what makes a cause obvious.

## Known issues

- **Reroll and other card-removing effects do not sync.** The card sync only knows how to
  add cards, so anything that removes, replaces or transforms your existing cards happens on
  the picker's screen and nowhere else. Fixing it needs a change to the sync format.
- **Cards that multiply their stats in `SetupCard`** can apply differently to the picker than
  to everyone else.
- **otDan-PickTimer is not compatible.** Its timer is disabled during picking and this mod
  uses its own.

## Building

Requires the .NET SDK and a ROUNDS install with BepInEx.

```
cd src
dotnet build -c Release -p:EpDebug=false
```

`EpDebug=false` strips the development tooling. Leave it off and you get an `F10` key that
simulates a reveal with fake players, which is handy for working on the visuals without
needing a lobby. Released builds bind no keys at all.

The build needs to find the game assemblies and UnboundLib. It defaults to the standard Steam
and Thunderstore locations; if yours differ, either create `src/Local.props`:

```xml
<Project>
  <PropertyGroup>
    <Profile>D:\path\to\your\profile</Profile>
  </PropertyGroup>
</Project>
```

or pass them on the command line with `-p:Profile=...` and `-p:GameManaged=...`.

Target `netstandard2.0` deliberately. The game runs an older Mono, and targeting anything
newer lets the compiler bind to methods that do not exist at runtime, which fails silently.

## Licence

MIT. See [LICENSE](LICENSE).
