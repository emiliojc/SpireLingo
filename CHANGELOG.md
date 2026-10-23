# Changelog

All notable changes to SpireLingo. Versions follow [Semantic Versioning](https://semver.org/); the version
lives in `SpireLingo.json` and is copied into the DLL at build time.

## 0.5.0 - 2026-10-23

### Added
- Vocabulary screen: your saved words, all of them in the All tab (where it opens) or by state (Learning,
  Recognised, Known, Looked up, Seen, Ignored), with search (just type) and sorting by last met, look-ups or how
  common the word is in the game. Clicking a word opens its page: base form with article and plural, all
  meanings, compound parts or separable-verb note, the Wiktionary example, up to five different game sentences
  you met it in with their English line, its history, and the Learning · Known · Ignore chips. Esc or Close
  leaves it.
- A "Vocabulary" button in the main menu (below Compendium) and on the discovery badge open it.
- A discovery button with the game's magnifying-glass icon turns discovery mode on and off and names its key
  when hovered: in the top bar during a run (between the timer and the map), and in the top-right corner on
  screens without a top bar, such as the main menu. A setting hides it.

## 0.4.0 - 2026-10-23

### Added
- Saved words: every word you click in discovery mode is remembered per language pair in
  `user://SpireLingo/words_deu_eng.json` (on Windows under `%APPDATA%\SlayTheSpire2`), with how often you looked
  it up, how often you saw it while reading, where in the game you met it, and a history of its states.
- Word states: seen, looked up, learning, recognised, known and ignored. The popup has Learning, Known and Ignore
  chips; clicking the lit chip takes the word back to "looked up". Looking up a known or recognised word moves it
  back to learning.
- The popup shows how many times you looked the word up.

## 0.3.1 - 2026-10-23

### Changed
- Finding the word under the mouse no longer walks the whole scene tree on every mouse move; the mod keeps
  track of the game's text labels instead.
- Rich text is measured once per lookup instead of once per line.

### Fixed
- In discovery mode, moving onto an open tooltip no longer opens the tooltip of an enemy lying under it.
- The highlight on a separable verb's other half no longer stays on screen after leaving discovery mode.
- At most 16 enemy-intent translations wait to be shown, so they can't pile up.

## 0.3.0 - 2026-10-23

### Added
- Glossary in the discovery popup: the word's base form (with article and plural for nouns), part of speech
  and numbered English meanings, from English Wiktionary (via kaikki.org, CC BY-SA 4.0).
- Compound words without an entry are explained by their parts (Nachziehstapel = nach + ziehen + Stapel).
- Separable verbs: clicking either half ("Füge" or "zu" in "Füge 6 Schaden zu.") shows the whole verb
  (zufügen), underlines the other half on screen, and explains that the particle moves to the end.
- `tools/build_glossary.py` builds the glossary and sentence data from the game's German text, and the README
  explains how to rebuild them. Covers 88.5% of the game's 7,716 distinct German words.

## 0.2.0 - 2026-10-23

### Added
- Discovery mode: press F8 (configurable) to pause input and dim the screen, point at any word on screen to
  highlight it, and click it for a popup. Esc or F8 leaves.
- "Show sentence" in the popup shows the whole sentence in the target language, with the game's numbers.
- Settings for the discovery key and how much the screen dims.

### Changed
- Translation tooltips are hidden while discovery mode is on.
- In discovery mode, tooltips open and close as usual but stay while the mouse is on them, and a card stays
  lifted while one of its words is selected.

## 0.1.0 - 2026-10-23

### Added
- Translation tooltip: hovering a card, relic, potion, power or enemy intent shows its text in a second
  language, with the same numbers as the game, using the game's own translations.
- Settings for the target language and which tooltips to show.
