# Ring Draft

A Slay the Spire 2 co-op mod. Players sit in a ring, and the player before you makes your build decisions:

| Decision | Who decides |
|---|---|
| Card reward (pick 1 of 3, or skip) | **Picker** |
| Shop: buy cards, relics, potions | **Picker**, spending **your** gold |
| Shop: card removal (and which card) | **Picker** |
| Smith upgrade: which card | **Picker**, but only after **you** choose Smith over Rest |
| Attack / Skill / Power potion choice | **Picker** |
| Toolbox-style relic choices at combat start | **Picker** |
| Card-effect choices (Discovery-style) | You (configurable) |
| Combat turns, potion use, targeting | You |
| Event options, event card rewards | You |
| Chest relics, elite relics, gold, potion rewards | You |
| Rest vs Smith, map path | You |

With 2 players it's a swap. With 3–4 it's a ring in seat order (P1→P2→P3→P4→P1). Everyone picks for their neighbour at the same time, so nobody waits.

**Everyone in the lobby needs the mod.** It sets `affects_gameplay: true`.

## Build and install (Windows)

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download) and Slay the Spire 2 installed through Steam.

```powershell
cd $HOME\Documents\GitHub\RingDraft
dotnet build -c Release
# Different Steam library? Point at it:
dotnet build -c Release -p:Sts2Dir="D:\SteamLibrary\steamapps\common\Slay the Spire 2"
```

The build copies `RingDraft.dll` and `RingDraft.json` into `<game>\mods\RingDraft\`. Launch with **Play with Mods**, enable Ring Draft in the Mods menu, restart, and host a co-op run. Send the same two files to everyone else.

Core logic tests run without the game:

```powershell
dotnet run --project tests\CoreTests
```

## How it works

Authority never moves. When your card reward screen opens, your client covers it with a "Alex is picking your card reward…" panel and sends the options to your picker over the game's own co-op channel. The picker sees a window listing the cards (tooltips show text), your gold, relics, and deck. Their click comes back as a message; your client validates it (right request, right sender, in range) and then presses the card on your own screen. To the game it looks like you clicked, so vanilla sync, RNG, and saves are untouched.

The shop works the same way, but it stays open for repeated actions. Each purchase runs through your merchant entry on your client, and a fresh shop state goes back to the picker. **Done shopping** returns control to you.

If a picker stalls (90s, or 30s in combat), you get a "Choose myself" button. Nothing is ever chosen automatically.

```
src/Core/   pure C#, no game refs: ring order, rules, wire codec, validation (tested)
src/Game/   Godot + sts2.dll: screen detection, networking, UI, applying picks
tests/      dependency-free test runner for src/Core
```

## Config

`ringdraft_config.json` is created in the game's user data folder on first launch (the path is logged in `ringdraft_log.txt` next to it). Each player's file governs their own screens.

```json
{
  "DelegateCardRewards": true,
  "DelegateEventCardRewards": false,
  "DelegateShop": true,
  "DelegateSmith": true,
  "DelegatePotionChoices": true,
  "DelegateRelicChoices": true,
  "DelegateCardEffectChoices": false,
  "DelegateUnknownCombatChoices": true,
  "ReverseRing": false,
  "TimeoutSeconds": 90,
  "CombatTimeoutSeconds": 30
}
```

## First build: what to check

The networking, screen classes, shop entries, and click/signal mechanics are copied from public, working StS2 mods (`sts2_typing`, `sts2-ai-mod`, `BetterSpire2`). Lines marked `// confirmed` in the code follow those mods. A few names are educated guesses accessed through reflection, so a wrong guess degrades gracefully instead of crashing. They're marked `// VERIFY`; check them against a decompile of `sts2.dll` (ILSpy or dnSpy):

1. **Namespaces.** If a `using MegaCrit...` line fails to compile, the type lives elsewhere. Search the decompile and fix the using.
2. **Deck and relics** (`GameQuery.MyDeck`, `MyRelics`). If the picker's "Their deck" button says *not available*, find the player's deck property.
3. **Card description** (`GameQuery.View`). Empty tooltips mean `Description` isn't the right member.
4. **Potion and card-play hooks** (`SourceTracker`). The log says which method got patched. If neither did, potion choices count as "unknown", which is still delegated by default, but Discovery-style card choices will be too.
5. **Event room detection** (`GameQuery.Room`) matches on the class name containing "Event".
6. **Synthetic clicks** (`UiDriver.Click`). Used for Skip and Confirm. If Skip doesn't register, swap in whatever `sts2-ai-mod`'s `GodotMainThread.ClickAsync` does.

## Known gaps

- Cards in the picker window are text buttons, not rendered cards. The code to render real card art exists in `sts2_typing`'s `ChatPanel` (`card_hover_tip.tscn`).
- Controller input on the owner's blocked screen is mostly, not completely, swallowed.
- If the same card appears twice in your deck, shop removal picks the first matching copy (by name and upgrade level).
- Reopening the merchant starts a new shopping trip for the picker.
