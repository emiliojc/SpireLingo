# SpireLingo

A Slay the Spire 2 mod for learning a language while you play. It adds an extra tooltip to cards, relics, potions, powers and enemy intents that shows their text in a second language, using the game's own official translations. Play in the language you are learning, and check the translation whenever you get stuck.

Example: with the game set to German and the target language set to English, hovering a card shows a "Translation" tooltip with its English name and text, with the same numbers as the card.

**Discovery mode**: press **F8** (configurable) to pause input and dim the screen, then point at any word on screen (cards, tooltips, events, dialogue, menus) to highlight it and click it for a popup. The popup shows the word's base form (with article and plural for nouns), its English meanings, the parts of compound words, and for separable verbs like "Füge … zu" (zufügen) the other half of the verb, marked on screen. A "Show sentence" button shows the whole sentence in the target language. Press F8 again or Esc to leave. Word meanings are currently German to English.

**Vocabulary**: every word you click is saved with its state (Learning, Known, Ignore, or looked up/seen automatically). Open the Vocabulary screen from the main menu or the discovery badge to browse all your words or one state at a time, search them, and read each word's page with all its meanings, an example, and the game sentences you met it in. The "Review" button there goes through the words you are learning with real game sentences, and brings each word back after 1, 3, 7 and 21 days until it is Known; the "Worth learning" tab suggests what to save next. In discovery mode, your saved words are underlined by state (orange for Learning, green for Known, teal for Recognised, grey for Looked up), so you can see how much of a card you can already read. A magnifier button in the top bar (or the top-right corner outside a run) also turns discovery mode on.

See [CHANGELOG.md](CHANGELOG.md) for what changed in each version.

Requires [BaseLib](https://github.com/Alchyr/BaseLib-StS2).

## Settings

In the mod config menu (from BaseLib):

- **Target language**: the language shown in the extra tooltip (default English). Restart the game after changing it.
- **Translation tooltips**: turn all "Translation" tooltips off at once, to learn only with discovery mode and the vocabulary screen (default on).
- **Show translation on**: turn the tooltip on or off for cards, relics, potions, powers and enemy intents.
- **Discovery key**: the key that turns discovery mode on and off (default F8). F2 isn't offered because the game opens its feedback screen with it.
- **Discovery dimming**: how much the screen darkens in discovery mode (0 to 0.5, default 0.2).
- **Discovery button**: show the magnifier button that turns discovery mode on (default on).
- **Word state colours**: in discovery mode, underline saved words by state (default on).

The tooltip only appears when the game's language is different from the target language.

## Releasing a version

1. Raise `version` in `SpireLingo.json` (it is copied into the DLL when building).
2. Add a section for it at the top of `CHANGELOG.md`.
3. Build and publish as below.

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
- Discovery mode is a node on its own canvas layer above the game. While it is on, it takes all input before the game does, and keeps open tooltips from closing. It finds the topmost text label under the mouse and works out the word from the label's character positions, without changing the label's text.
- "Show sentence" matches the clicked sentence against the game's own text in its current language, with `{placeholders}` as wildcards, then fills the same entry in the target language with the captured numbers.
- Word meanings come from `SpireLingo/data/glossary_deu_eng.json`, and the analysis of each line of the game's German text (base forms, which words belong together) from `SpireLingo/data/sentences_deu.json`. Both are generated by `tools/build_glossary.py` (see below).
- Code is in `SpireLingoCode/`; the mod's own text is in `SpireLingo/localization/`.

## Rebuilding the glossary

The data files are generated once and committed; rebuild them after a game update changes its text.

1. Download the German extract of English Wiktionary from [kaikki.org](https://kaikki.org/dictionary/German/) (`kaikki.org-dictionary-German.jsonl.gz`, about 100 MB).
2. In a Python 3.10+ virtual environment outside the repo:
   ```
   pip install "spacy>=3.8,<3.9"
   python -m spacy download de_core_news_md
   ```
3. From the repo folder:
   ```
   python tools/build_glossary.py --pck "<Steam>/steamapps/common/Slay the Spire 2/SlayTheSpire2.pck" --kaikki kaikki.org-dictionary-German.jsonl.gz
   ```
   It prints how many of the game's words have an entry and the most frequent ones without one.

Game-specific abbreviations and meanings ("TP" = HP, "Orb", "Zug" = turn) are set by hand in `GAME_TERMS`,
`GAME_TERM_FORMS` and `GAME_SENSES` at the top of the script. After changing them, apply them to the committed
files without a full rebuild: `python tools/build_glossary.py --game-terms-only`.

## Credits

Word meanings are from [English Wiktionary](https://en.wiktionary.org/), extracted by [kaikki.org](https://kaikki.org/) ([Wiktextract](https://github.com/tatuylonen/wiktextract)), and licensed under [CC BY-SA 4.0](https://creativecommons.org/licenses/by-sa/4.0/). See `NOTICE`.

## Roadmap

Done:

1. Translation tooltips.
2. Discovery mode: click any word on screen for its meaning, with an offline glossary built from the game's text.
3. Saved words with states, the vocabulary screen, state underlines and spaced reviews.

Next:

4. Usage tips and related words on each word's page.
5. Anki export of saved words.
6. Other language pairs: choose the translation language (English today) and the game's language (German today), with a glossary for each pair.
7. Publishing on the Steam Workshop.
