# Ring Draft

**[Get it on the Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3818802815)**

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

Or just double-click `build.bat` (output goes to `build-log.txt`). The build copies `RingDraft.dll` and `RingDraft.json` into `<game>\mods\RingDraft\` and into `workshop\content\`. If you're also subscribed on the Workshop, delete the `mods\RingDraft` copy before playing so the mod isn't loaded twice.

Launch with **Play with Mods**, enable Ring Draft in the Mods menu, restart, and host a co-op run.

To publish an update to the Workshop, run `upload.bat`. It needs Mega Crit's [ModUploader](https://github.com/megacrit/sts2-mod-uploader/releases) unzipped into `tools\ModUploader\` and Steam running.

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

## Verified against the game

Every game type, member, signal and Harmony target the mod uses was checked against the metadata of `sts2.dll` **v0.107.1**, and the mod compiles cleanly against the real `sts2.dll`, `GodotSharp.dll` and `0Harmony.dll` (warnings as errors). The screens it watches are:

| Screen | Game class |
|---|---|
| Card reward | `NCardRewardSelectionScreen` |
| Potion / Toolbox / Discovery "choose a card" | `NChooseACardSelectionScreen` |
| Smith upgrade | `NDeckUpgradeSelectScreen` (only appears after Smith is chosen) |
| Shop removal | any `NCardGridSelectionScreen` opened by a removal the picker bought |
| Shop | `NMerchantRoom.Instance.Inventory` while `IsOpen` |

Potion use and card plays are timed through Harmony prefixes on `PotionModel.OnUseWrapper` and `CardModel.OnPlayWrapper`.

What hasn't been exercised is the live game. Expect to tune behavior on the first co-op run. `ringdraft_log.txt` logs every hand-off, every pick applied, and every rejected message. If a game update renames something, the mod fails to compile rather than misbehaving quietly.

## Known gaps

- Cards in the picker window are text buttons, not rendered cards. The code to render real card art exists in `sts2_typing`'s `ChatPanel` (`card_hover_tip.tscn`).
- Controller input on the owner's blocked screen is mostly, not completely, swallowed.
- If the same card appears twice in your deck, shop removal picks the first matching copy (by name and upgrade level).
- Reopening the merchant starts a new shopping trip for the picker.
