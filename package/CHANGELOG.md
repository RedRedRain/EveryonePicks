v0.3.36
---

- New icon.

v0.3.35
---

- Added a link to the bug tracker, where you can now report problems.

v0.3.34
---

- Fixed a crash in another mod being able to end your match: empty map, no players, no more cards.
- A client that falls out of sync now recovers itself instead of being stuck for the rest of the game.
- Fixed the mod crashing on startup, which stopped error messages naming the mod at fault.
- No more "waiting on" messages after the round has already moved on.
- The pick board says "overdue" instead of "0s" once someone's time is up.

v0.3.33
---

- Big red WAITING text while other players are still choosing.
- Hover a player's stack of cards to spread them apart, so you can read each one.
- Cards from one player no longer overlap each other.
- Shorter waiting and error messages.

v0.3.26
---

- Picked cards are drawn at a sensible size instead of tiny.
- Added a card size slider in Mod Options.
- Multiple cards from one player now fan out under that player instead of appearing as
  separate players.
- Player name now sits above their model.
- Large lobbies wrap onto multiple rows instead of squashing into one.

v0.3.19
---

- Picked cards now show on screen as each player locks one in.
- A player quitting no longer breaks the match for everyone else.
- Cards that other mods hand you during a pick now sync properly instead of applying only on
  your own screen. Fixes Distill Acquisition and anything similar.
- Things a card spawns during the pick (swords, minions, effects) are no longer created
  locally and thrown away. Fixes Swordsman Class, TimeToGrind minions, Life Link and others.
- Extra picks that were still queued can no longer be lost when the round ends.
- Null cards and other mods that build a card on spawn now initialise correctly.
- Pick time is taken from each player's own setting, not the host's.
- When a mod throws during the pick phase it now says which mod, on screen.
- Warns if players in the lobby are on different versions of this mod.
- Says who the round is waiting on instead of just appearing frozen.
- Added settings to Mod Options.

v0.3.11
---

- Simplified the details page and changelog.

v0.3.9
---

- Everyone starts picking at the same time. It used to walk the lobby one player at a time.
- Cards now appear as each player picks, with that player shown above their card.
- Added a pick board in the bottom left showing who has picked and who you are waiting on.
- A player quitting no longer breaks the match for everyone else.
- Extra picks from other mods no longer get thrown away or freeze the lobby.
- Rounds no longer hang after everyone has picked.
- otDan-PickTimer no longer breaks the pick phase.
- Added settings to Mod Options.

v0.2.1
---

- Fixed result indexing, deadlines, curse relay, host migration and leaver handling.

v0.2.0
---

- Rebuilt on the game's real pick flow, so card animations, sounds and controller input work.
- Host authoritative results, so every client ends up with the same deck.

v0.1.0
---

- Deprecated. Replaced the pick UI and caused freezes.
