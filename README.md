# SpireLingo

A Slay the Spire 2 mod for learning a language while you play. It adds an extra tooltip to cards, relics, potions, powers and enemy intents that shows their text in a second language, using the game's own official translations. Play in the language you are learning, and check the translation whenever you get stuck.

Example: with the game set to German and the target language set to English, hovering a card shows a "Translation" tooltip with its English name and text, with the same numbers as the card.

Requires [BaseLib](https://github.com/Alchyr/BaseLib-StS2).

## Settings

In the mod config menu (from BaseLib):

- **Target language**: the language shown in the extra tooltip (default English). Restart the game after changing it.
- **Show translation on**: turn the tooltip on or off for cards, relics, potions, powers and enemy intents.

The tooltip only appears when the game's language is different from the target language.

## Building

You need the .NET SDK (9 or newer), [MegaDot](https://github.com/Alchyr/ModTemplate-StS2/wiki/Setup) 4.5.1, and BaseLib installed in the game (for example by subscribing on the Workshop).

1. Copy `Directory.Build.props.example` to `Directory.Build.props` and set `GodotPath` to your MegaDot executable. If the game is not in the default Steam folder, also set `Sts2Path`.
2. Build the code and copy the `.dll` and `.json` into the game's `mods/SpireLingo` folder:
   ```
   dotnet build
   ```
3. To also make the `.pck` (needed for the mod's own text files), publish:
   ```
   dotnet publish SpireLingo.csproj
   ```
4. Start the game with mods enabled and check the log for `[SpireLingo] Loaded`.

## How it works

- When the game loads a text table in its own language (say `localization/deu/cards.json`), the mod also loads the same table in the target language (`localization/eng/cards.json`).
- When a tooltip is built, the mod runs the game's own text formatting again while answering text lookups from the target-language tables. That way numbers like damage and block match what the card shows.
- Code is in `SpireLingoCode/`; the mod's own text is in `SpireLingo/localization/`.

## Roadmap

1. Translation tooltip (this version).
2. Hotkey search box for single words, with an offline glossary built from the game's text.
3. Saved vocabulary list with Anki export.
