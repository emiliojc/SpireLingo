# Changelog

All notable changes to SpireLingo. Versions follow [Semantic Versioning](https://semver.org/); the version
lives in `SpireLingo.json` and is copied into the DLL at build time.

## 0.8.2 - 2026-10-24

### Fixed
- Discovery mode works in the Ancient's room at the start of each act (where you choose a boon): an empty or
  faded-out label lying over the dialogue no longer hides the words under it. Labels on top are now passed over
  only where they are see-through (no text, faded out, or the mouse is outside their text); a card or tooltip
  still hides the text behind it.

## 0.8.1 - 2026-10-24

### Changed
- The Wortschatz (Vocabulary) button moved from the main menu into the Kompendium, after Statistics.
- The magnifier no longer shows on the main menu, where it distracted; F8 still turns discovery mode on there.
  During a run it stays in the top bar.

### Fixed
- Game words the glossary had wrong or missing: Buff and Debuff are the game's powers (not "Puff" or a neck
  gaiter), Fogl is the game's Byrd (with Foglonis, Foglpip and Foglgeist), "schlurfendes" is schlurfend
  (shuffling) instead of a split into "Schlurfen" + "Des", and "davonjagt" is davonjagen (to chase off).

## 0.8.0 - 2026-10-24

### Changed
- The discovery popup is shorter: it shows only the meaning that fits the sentence, picked by matching the
  meanings against the sentence's English line ("Zug" in "in einem Zug" is "turn"), with any labels such as
  "colloquial" and a note of how many more meanings the vocabulary screen has.

### Added
- The word page in the vocabulary screen has every meaning with its labels, usage tips (learn a noun with its
  article and plural, separable verbs, dative/accusative/genitive, reflexive and impersonal use, colloquial
  or formal meanings), the words the game most often uses it with, its parts, up to three examples, and
  related words: similar words, opposites and the word family, including the game's own compounds.
- Related words are links: clicking one opens its page, where it can be saved with the state buttons.
- The glossary carries Wiktionary's sense labels, more examples, related words and short word-part
  etymologies. `tools/build_glossary.py --enrich-only --kaikki ...` adds them to the existing data without
  reading the game again.

## 0.7.1 - 2026-10-24

### Fixed
- The vocabulary screen always opens on the word list. A review left open when the screen closed no longer
  comes back on top of the list and takes the keys meant for the search.

## 0.7.0 - 2026-10-24

### Added
- Reviews: a "Review" button on the vocabulary screen goes through your due Learning and Recognised words (up to
  20 at a time). Each card shows a game sentence you met the word in with the word marked; "Show answer" (Space)
  reveals its meanings, the sentence in English and an example, then "Knew it" (1) or "Didn't" (2). A new word
  comes back at once, then after 1, 3, 7 and 21 days: passing the 7-day review makes it Recognised, passing the
  21-day one makes it Known. "Didn't" brings it back tomorrow, and a Recognised word goes back to Learning.
- A Learning word you read 8 times in discovery mode without looking it up becomes Recognised on its own.
- A "Worth learning" tab lists words you met or looked up but haven't saved, ordered by how often the game uses
  them, leaving out small words like der, und and in.
- From the third look-up of a word you haven't saved, the popup's counter suggests saving it.
- The word page shows when the word is next reviewed.

## 0.6.2 - 2026-10-24

### Fixed
- The glossary knows the game's own abbreviations: "TP" is HP (Trefferpunkte), "Orb" is the Defect's orb
  instead of "Object Request Broker", and the settings' FPS, MSAA, Esc and the card FTL have meanings.
- "Zug", "Block" and "Bug" show their game meaning first: a turn, the Block that protects you, and a bug in
  the game.

## 0.6.1 - 2026-10-23

### Added
- A "Translation tooltips" setting that hides every "Translation" tooltip at once, for learning only with
  discovery mode and the vocabulary screen. The per-type switches apply only while it is on.

### Fixed
- The glossary no longer turns short words and abbreviations into place or person names: "kl." is klein,
  "mi." mittel, "max." maximal, and die, so, alle, groß and aus are the ordinary words again (not the Algarve,
  a Swiss canton, a river, a surname or an Austrian town). (#8)
- The discovery popup moves up instead of running off the bottom of the screen when "Show sentence" makes it
  taller, and energy icons show as icons in it instead of "( )". (#9)
- On the main menu the magnifier sits beside the patch notes button instead of on top of it. (#10)
- The top-bar magnifier is a little smaller, like its neighbours, and shows the game's own tooltip with its
  key. (#12)

## 0.6.0 - 2026-10-23

### Added
- Word state colours in discovery mode: saved words on screen are underlined by state, so you see at a glance
  how much of a card you can read. Learning is orange, Known green, Recognised faint teal, Looked up a thin
  faint grey; Ignored, Seen and unsaved words stay unmarked. Underlines are drawn with their label, so a card or
  tooltip in front hides them. Words are matched to their base form like in the popup,
  so both halves of a separable verb ("Füge ... zu") are marked. Changing a word's state updates its marks
  right away. A setting turns the colours off.

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
