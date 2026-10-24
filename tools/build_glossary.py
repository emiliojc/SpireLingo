"""Builds the discovery-mode word data from the game's German text and English Wiktionary.

Outputs (shipped in the mod's .pck):
  SpireLingo/data/glossary_deu_eng.json   lemma -> part of speech, gender/plural, numbered English senses;
                                          plus inflected form -> lemma and compound word -> parts
  SpireLingo/data/sentences_deu.json      per game text line: its words with lemma, part of speech and
                                          links between words that belong together (separable verbs)

Usage (see README, "Rebuilding the glossary"):
  python tools/build_glossary.py --pck ".../Slay the Spire 2/SlayTheSpire2.pck" \
      --kaikki kaikki.org-dictionary-German.jsonl.gz
"""

import argparse
import gzip
import json
import re
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
OUT_DIR = REPO / "SpireLingo" / "data"

MAX_SENSES = 6
MAX_EXAMPLES = 3
MAX_RELATED = 6
# Sense labels worth showing a learner, from Wiktionary's tags (others, like "error-unknown-tag", are dropped).
USEFUL_TAGS = {"colloquial", "informal", "formal", "figuratively", "transitive", "intransitive", "reflexive",
               "impersonal", "dated", "archaic", "obsolete", "rare", "regional", "slang", "vulgar", "derogatory",
               "humorous", "literary", "poetic", "with-dative", "with-accusative", "with-genitive", "Austria",
               "Switzerland", "Southern-Germany", "Northern-Germany", "plural-only", "singular-only", "auxiliary",
               "separable", "inseparable"}
# Wiktionary's lists of words that belong with a word, under the names the mod uses.
RELATIONS = {"synonyms": "synonyms", "antonyms": "antonyms", "derived": "derived", "related": "related",
             "hyponyms": "derived", "coordinate_terms": "related"}
MAX_FORMS = 6
WORD = re.compile(r"[^\W\d_]+(?:[-'’][^\W\d_]+)*")
TAG = re.compile(r"\[[^\[\]]*\]")
# Parts of speech kept from Wiktionary, mapped to short labels the mod shows.
POS = {"noun": "noun", "name": "name", "verb": "verb", "adj": "adj", "adv": "adv", "prep": "prep",
       "pron": "pron", "det": "det", "article": "det", "conj": "conj", "particle": "particle", "num": "num",
       "intj": "intj", "prep_phrase": "phrase", "phrase": "phrase", "postp": "prep", "contraction": "contraction"}
SPACY_POS = {"NOUN": "noun", "PROPN": "name", "VERB": "verb", "AUX": "verb", "ADJ": "adj", "ADV": "adv",
             "ADP": "prep", "PRON": "pron", "DET": "det", "CCONJ": "conj", "SCONJ": "conj", "PART": "particle",
             "NUM": "num", "INTJ": "intj"}
LINKING = ["", "s", "n", "en", "e", "es", "er"]
# Particles that split off a verb and go to the end of the clause ("Füge ... zu" = "zufügen").
PARTICLES = {"ab", "an", "auf", "aus", "bei", "dar", "ein", "fest", "fort", "frei", "her", "herab", "heran", "heraus",
             "herbei", "herein", "hin", "hinaus", "hinein", "hinzu", "hoch", "los", "mit", "nach", "nieder", "vor",
             "voran", "voraus", "vorbei", "weg", "weiter", "wieder", "zu", "zurück", "zusammen"}
# Abbreviations in the game's text and the words they stand for ("Zweigschleim (kl.)", "max. TP").
ABBREVIATIONS = {"kl": "klein", "mi": "mittel", "max": "maximal"}
# Short words that mean something special in the game, where Wiktionary has nothing or the wrong thing
# ("TP" is missing, "Orb" is read as "Object Request Broker"). These entries replace Wiktionary's.
GAME_TERMS = {
    "TP": {"pos": "noun", "plural": "TP", "senses": ["HP, hit points (short for Trefferpunkte)"]},
    "Orb": {"pos": "noun", "gender": "m", "plural": "Orbs", "senses": ["orb (channelled by the Defect)"]},
    "FPS": {"pos": "noun", "senses": ["FPS, frames per second (short for Bilder pro Sekunde)"]},
    "MSAA": {"pos": "noun", "senses": ["MSAA, multisample anti-aliasing"]},
    "ESC": {"pos": "noun", "senses": ["Esc, the Escape key"]},
    "FTL": {"pos": "noun", "senses": ["FTL, faster than light"]},
}
# Spellings of the game terms in the text ("ORBS" in dialogue, "tP" after sentence-start lowercasing).
GAME_TERM_FORMS = {"tp": "TP", "orb": "Orb", "orbs": "Orb", "fps": "FPS", "msaa": "MSAA", "esc": "ESC", "ftl": "FTL"}
# The game's meaning of an ordinary word, shown before Wiktionary's senses ("in einem Zug" = in one turn).
GAME_SENSES = {
    "Zug": "turn (in a fight)",
    "Block": "Block (protects you from damage)",
    "Bug": "bug (an error in the game)",
}
SENTENCE_START = re.compile(r"(^|[.!?:]\s+)([A-ZÄÖÜ])")


def lower_sentence_starts(text):
    """The game's text is mostly commands ("Füge 6 Schaden zu."); spaCy reads a capitalised first word as a
    noun, so sentence starts are lowercased for parsing. Lengths stay the same."""
    return SENTENCE_START.sub(lambda m: m.group(1) + m.group(2).lower(), text)


# ---------------------------------------------------------------- game text

def read_pck_tables(pck_path, lang):
    """Reads localization/<lang>/*.json from a Godot 4 .pck (format version 3, unencrypted)."""
    tables = {}
    with open(pck_path, "rb") as f:
        magic, version, _major, _minor, _patch, flags, base, dir_offset = struct.unpack("<4sIIIIIQQ", f.read(40))
        if magic != b"GDPC" or version < 3:
            sys.exit(f"{pck_path}: not a Godot 4 .pck this script understands")
        f.seek(dir_offset)
        (count,) = struct.unpack("<I", f.read(4))
        entries = []
        for _ in range(count):
            (length,) = struct.unpack("<I", f.read(4))
            path = f.read(length).rstrip(b"\0").decode()
            offset, size = struct.unpack("<QQ", f.read(16))
            f.read(16)
            (file_flags,) = struct.unpack("<I", f.read(4))
            entries.append((path, offset + (base if flags & 2 else 0), size, file_flags))
        pattern = re.compile(rf"^(?:res://)?localization/{lang}/([^/]+)\.json$")
        for path, offset, size, file_flags in entries:
            m = pattern.match(path)
            if not m:
                continue
            if file_flags & 1:
                sys.exit(f"{path} is encrypted")
            f.seek(offset)
            tables[m.group(1)] = json.loads(f.read(size).decode("utf-8-sig"))
    return tables


def split_placeholders(raw):
    """Yields (is_placeholder, text) parts; placeholders can nest."""
    start = depth = 0
    for i, c in enumerate(raw):
        if c == "{":
            if depth == 0:
                if i > start:
                    yield False, raw[start:i]
                start = i
            depth += 1
        elif c == "}" and depth > 0:
            depth -= 1
            if depth == 0:
                yield True, raw[start + 1:i]
                start = i + 1
    if start < len(raw):
        yield depth > 0, raw[start:]


def split_top(text, sep):
    parts, depth, start = [], 0, 0
    for i, c in enumerate(text):
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif c == sep and depth == 0:
            parts.append(text[start:i])
            start = i + 1
    parts.append(text[start:])
    return parts


def lines_of(raw):
    """Splits at line breaks outside placeholders, the same way the mod does."""
    lines, depth, start = [], 0, 0
    for i, c in enumerate(raw):
        if c == "{":
            depth += 1
        elif c == "}" and depth > 0:
            depth -= 1
        elif c == "\n" and depth == 0:
            lines.append(raw[start:i])
            start = i + 1
    lines.append(raw[start:])
    return lines


def render(raw):
    """A readable sample of a line: numbers for values, the first non-empty option for choices."""
    out = []
    for is_placeholder, part in split_placeholders(raw):
        if not is_placeholder:
            out.append(part)
            continue
        pieces = split_top(part, ":")
        if len(pieces) >= 3:
            options = split_top(":".join(pieces[2:]), "|")
            option = next((o for o in options if o.strip()), "")
            out.append(render(option.replace("{}", "2")))
        else:
            out.append("2")
    return re.sub(r"\s+", " ", TAG.sub("", "".join(out))).strip()


# ---------------------------------------------------------------- Wiktionary

def kaikki_items(path):
    opener = gzip.open if str(path).endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as f:
        for line in f:
            item = json.loads(line)
            if item.get("lang_code") == "de" and item.get("word"):
                yield item


def kaikki_lemma_words(path):
    """Every German word Wiktionary has a real entry for (not just "form of ..."), for splitting compounds."""
    words = set()
    for item in kaikki_items(path):
        if any(not (s.get("form_of") or s.get("alt_of")) and s.get("glosses") for s in item.get("senses", [])):
            words.add(item["word"])
    return words


def load_kaikki(path, wanted, match_forms=True):
    """Reads the kaikki.org German extract, keeping entries for the wanted words (any case), and with
    match_forms also entries that list a wanted word among their inflected forms."""
    wanted_lower = {w.lower() for w in wanted}
    entries = defaultdict(list)   # lemma -> [entry]
    form_of = defaultdict(set)    # form (lower) -> lemmas
    for item in kaikki_items(path):
        word = item["word"]
        relevant = word.lower() in wanted_lower
        if match_forms and not relevant:
            forms = [x.get("form", "") for x in item.get("forms", []) if x.get("form")]
            relevant = any(x.lower() in wanted_lower for x in forms)
            # Separable verbs never appear whole in commands, so keep them all; unused ones are dropped later.
            relevant = relevant or (item.get("pos") == "verb" and any(word.startswith(p) for p in PARTICLES))
        if not relevant:
            continue
        senses, tags, lemma_targets = [], [], set()
        for sense in item.get("senses", []):
            targets = sense.get("form_of") or sense.get("alt_of")
            if targets:
                lemma_targets.update(t["word"] for t in targets if t.get("word"))
                continue
            gloss = (sense.get("glosses") or [""])[0].strip()
            if gloss and gloss not in senses:
                senses.append(gloss)
                tags.append(sorted(set(sense.get("tags", [])) & USEFUL_TAGS))
        for target in lemma_targets:
            form_of[word.lower()].add(target)
        if not senses:
            continue
        pos = POS.get(item.get("pos", ""), item.get("pos", ""))
        entry = {"pos": pos, "senses": senses[:MAX_SENSES]}
        tags = tags[:MAX_SENSES]
        if any(tags):
            entry["tags"] = tags
        if pos == "noun":
            gender = gender_of(item)
            if gender:
                entry["gender"] = gender
            plural = next((x["form"] for x in item.get("forms", []) if "plural" in x.get("tags", [])
                           and "nominative" in x.get("tags", []) and x.get("form") != "-"), None)
            if plural:
                entry["plural"] = plural
        for x in item.get("forms", []):
            form = x.get("form", "")
            if form and form != word and WORD.fullmatch(form):
                form_of[form.lower()].add(word)
        examples = examples_of(item)
        if examples:
            entry["example"] = examples[0]
            if len(examples) > 1:
                entry["examples"] = examples
        related = related_of(item)
        if related:
            entry["related"] = related
        etymology = short_etymology(item)
        if etymology:
            entry["etymology"] = etymology
        entries[word].append(entry)
    return entries, form_of


def gender_of(item):
    tags = set()
    for template in item.get("head_templates", []):
        args = template.get("args", {})
        for key in ("1", "g", "g2"):
            value = args.get(key, "")
            tags.update({"m": "m", "f": "f", "n": "n"}.get(v[:1], "") for v in value.split(","))
    for sense in item.get("senses", []):
        tags.update({"masculine": "m", "feminine": "f", "neuter": "n"}.get(t, "") for t in sense.get("tags", []))
    tags.discard("")
    return "".join(sorted(tags, key="mfn".index)) or None


def examples_of(item):
    """Up to MAX_EXAMPLES short example sentences with their English, at most one per sense."""
    examples = []
    for sense in item.get("senses", []):
        for example in sense.get("examples", []):
            text, english = example.get("text", ""), example.get("english") or example.get("translation")
            if text and english and len(text) < 120:
                examples.append({"de": text, "en": english})
                break
        if len(examples) == MAX_EXAMPLES:
            break
    return examples


def related_of(item):
    """Synonyms, antonyms, derived and related words from the entry and its senses, single words only, in
    Wiktionary's order. They are ranked by how much the game uses them later, in rank_related."""
    related = defaultdict(list)
    sources = [item] + item.get("senses", [])
    for source in sources:
        for key, kind in RELATIONS.items():
            for x in source.get(key, []):
                word = x.get("word", "").strip()
                if word and word != item["word"] and WORD.fullmatch(word) and word not in related[kind]:
                    related[kind].append(word)
    return {kind: words for kind, words in related.items() if words}


def short_etymology(item):
    """How the word is built, when the etymology's first sentence says so briefly ("From er- + halten.").
    Older forms and other languages are left out; they help a learner less than the parts do."""
    text = (item.get("etymology_text") or "").strip()
    first = re.split(r"(?<=[.;])\s", text, maxsplit=1)[0]
    return first if "+" in first and len(first) <= 120 else None


def rank_related(glossary, sentences):
    """Keeps the related words a player is likely to meet: those the game uses come first, most used first,
    then the rest in Wiktionary's order, MAX_RELATED of each kind."""
    frequency = Counter(lemma for tokens in sentences.values() for lemma in {tok[1] for tok in tokens})
    for entries in glossary.values():
        for entry in entries:
            related = entry.get("related")
            if not related:
                continue
            for kind, words in list(related.items()):
                ranked = sorted(words, key=lambda w: (-frequency.get(w, 0), words.index(w)))[:MAX_RELATED]
                related[kind] = ranked


# ---------------------------------------------------------------- analysis

def build(pck, kaikki_path, model_name):
    import spacy  # imported here so --help works without it

    deu = read_pck_tables(pck, "deu")
    print(f"read {len(deu)} German tables")

    samples = {}  # "table/key#line" -> rendered line
    for table, data in deu.items():
        for key, raw in data.items():
            for n, line in enumerate(lines_of(raw)):
                text = render(line)
                if WORD.search(text):
                    samples[f"{table}/{key}#{n}"] = text

    nlp = spacy.load(model_name, disable=["ner"])
    docs = list(nlp.pipe((lower_sentence_starts(t) for t in samples.values()), batch_size=256))
    print(f"parsed {len(docs)} lines")

    surface_counts = Counter()
    for text, doc in zip(samples.values(), docs):
        surface_counts.update(text[t.idx:t.idx + len(t)] for t in doc if WORD.fullmatch(t.text))
    wanted = set(surface_counts) | {t.lemma_ for doc in docs for t in doc if WORD.fullmatch(t.lemma_)} | set(ABBREVIATIONS.values())

    # Compounds the game made up ("Nachziehstapel") are split into words Wiktionary has, which are then
    # loaded too.
    lemma_words = kaikki_lemma_words(kaikki_path)
    lemma_lower = {w.lower() for w in lemma_words}
    in_wiktionary = lambda w: w in lemma_words or w.lower() in lemma_lower
    candidate_compounds = {}
    for word in surface_counts:
        if len(word) >= 7 and not in_wiktionary(word):
            parts = split_compound(word, in_wiktionary)
            if parts:
                candidate_compounds[word.lower()] = parts
    part_words = {p for parts in candidate_compounds.values() for p in parts}

    entries, form_of = load_kaikki(kaikki_path, wanted | part_words)
    # Words that are only "form of X" need X's own entry as well.
    targets = {t for lemmas in form_of.values() for t in lemmas} - set(entries)
    if targets:
        more, more_forms = load_kaikki(kaikki_path, targets, match_forms=False)
        for lemma, items in more.items():
            entries[lemma].extend(items)
        for form, lemmas in more_forms.items():
            form_of[form] |= lemmas
    print(f"kept {len(entries)} Wiktionary lemmas, {len(candidate_compounds)} compounds to split")
    lower_entries = defaultdict(list)
    for lemma in entries:
        lower_entries[lemma.lower()].append(lemma)

    def known(word):
        return word in entries or word.lower() in lower_entries

    def name_only(lemma):
        return lemma in entries and all(e["pos"] == "name" for e in entries[lemma])

    def lemma_for(token, surface):
        """Wiktionary lemma for a token, preferring spaCy's lemma when Wiktionary has it. A word written in
        lowercase on screen is never a name ("die" is not the Algarve, "aus" not the town of Au), and a
        capitalised one is a name only when no ordinary word fits."""
        if surface.lower() in ABBREVIATIONS:
            return ABBREVIATIONS[surface.lower()]
        candidates = [token.lemma_, token.text]
        if token.pos_ in ("NOUN", "PROPN"):
            candidates.append(token.lemma_[:1].upper() + token.lemma_[1:])
        else:
            candidates.append(token.lemma_.lower())
        # Ordinary words first; a name only for a capitalised word nothing else fits (so "Die" at the start of
        # a sentence is still the article, not the Algarve).
        passes = [False, True] if surface[:1].isupper() else [False]
        for allow_names in passes:
            usable = lambda lemma: lemma in entries and (allow_names or not name_only(lemma))
            for candidate in candidates:
                if usable(candidate):
                    return candidate
            # A word with an ordinary entry of its own isn't read as a form of another ("so" is not "So." for
            # Sonntag, "groß" not "Gros").
            own = token.text.lower() in entries and not name_only(token.text.lower())
            for target in [] if own else sorted(form_of.get(token.text.lower(), [])):
                if usable(target):
                    return target
            for candidate in candidates:
                for lemma in lower_entries.get(candidate.lower(), []):
                    if usable(lemma):
                        return lemma
        return token.lemma_ if surface[:1].isupper() else token.lemma_.lower()

    def verb_lemmas(token):
        """Infinitives a word can be a form of ("füge" -> "fügen")."""
        found = []
        for candidate in [token.lemma_.lower(), token.text.lower()] + sorted(form_of.get(token.text.lower(), [])):
            if candidate in entries and any(e["pos"] == "verb" for e in entries[candidate]) and candidate not in found:
                found.append(candidate)
        return found

    def is_verb(lemma):
        return lemma in entries and any(e["pos"] == "verb" for e in entries[lemma])

    sentences, verbs_used, compounds = {}, set(), {}
    stats = Counter()
    for (key, text), doc in zip(samples.items(), docs):
        tokens, index_of = [], {}
        for t in doc:
            if WORD.fullmatch(t.text):
                index_of[t.i] = len(tokens)
                tokens.append([t.text.lower(), lemma_for(t, text[t.idx:t.idx + len(t)]), SPACY_POS.get(t.pos_, t.pos_.lower())])

        def link(verb_token, particle_token, verb):
            v, p = index_of[verb_token.i], index_of[particle_token.i]
            if len(tokens[v]) > 3 or len(tokens[p]) > 3:
                return
            tokens[v][1] = tokens[p][1] = verb
            tokens[v][2] = "verb"
            tokens[p][2] = "particle"
            tokens[v].append(p)
            tokens[p].append(v)
            verbs_used.add(verb)

        # Separable verbs: spaCy marks the particle with the dependency "svp" ...
        for t in doc:
            if t.dep_ == "svp" and t.i in index_of and t.head.i in index_of:
                for verb in verb_lemmas(t.head):
                    if is_verb(t.text.lower() + verb):
                        link(t.head, t, t.text.lower() + verb)
                        stats["svp"] += 1
                        break
        # ... and when it doesn't, a particle ending a clause joins the clause's first or second word, if
        # Wiktionary has the combined verb.
        clause = []
        for t in list(doc) + [None]:
            if t is not None and not (t.is_punct and t.text in ".,;:!?"):
                if t.i in index_of:
                    clause.append(t)
                continue
            if len(clause) >= 2 and clause[-1].text.lower() in PARTICLES and len(tokens[index_of[clause[-1].i]]) == 3:
                particle = clause[-1]
                for candidate in clause[:2]:
                    if candidate is particle:
                        continue
                    verb = next((particle.text.lower() + v for v in verb_lemmas(candidate)
                                 if is_verb(particle.text.lower() + v)), None)
                    if verb:
                        link(candidate, particle, verb)
                        stats["rule"] += 1
                        break
            clause = []
        sentences[key] = tokens
    print(f"separable verbs linked: {stats['svp']} by spaCy, {stats['rule']} by the clause-end rule")

    # Words with no entry of their own are explained by their parts.
    for word in surface_counts:
        low = word.lower()
        if low in candidate_compounds and not known(word) and low not in form_of:
            compounds[low] = list(candidate_compounds[low])

    used_lemmas = {tok[1] for tokens in sentences.values() for tok in tokens} | verbs_used
    used_lemmas |= {p for parts in compounds.values() for p in parts}
    glossary = {}
    for lemma in sorted(used_lemmas):
        for candidate in [lemma] + lower_entries.get(lemma.lower(), []):
            if candidate in entries:
                glossary[candidate] = entries[candidate]
                break

    # Lowercase words never point at names ("kl" is not Kaiserslautern); abbreviations point at their word.
    lowercase_seen = {w.lower() for w in surface_counts if w[:1].islower()}
    forms = {}
    for word in surface_counts:
        low = word.lower()
        if low in glossary and not name_only(low):
            continue
        targets = sorted(t for t in form_of.get(low, []) if t in glossary and not (low in lowercase_seen and name_only(t)))
        if targets and low not in {g.lower() for g in targets}:
            forms[low] = targets[:3]
    for short, full in ABBREVIATIONS.items():
        if full in glossary:
            forms[short] = [full]

    add_game_terms(glossary, forms, sentences)
    rank_related(glossary, sentences)

    coverage = sum(1 for w in surface_counts if known(w) or w.lower() in form_of or w.lower() in compounds
                   or w.lower() in GAME_TERM_FORMS)
    report = {
        "distinct_words": len(surface_counts),
        "covered": coverage,
        "coverage_pct": round(100 * coverage / max(1, len(surface_counts)), 1),
        "lemmas": len(glossary),
        "forms": len(forms),
        "compounds": len(compounds),
        "separable_verbs": len(verbs_used),
    }
    return glossary, forms, compounds, sentences, report, surface_counts


def add_game_terms(glossary, forms, sentences):
    """Puts the game's own abbreviations and meanings in place of what Wiktionary and spaCy made of them."""
    for lemma, entry in GAME_TERMS.items():
        glossary[lemma] = [entry]
    for word, lemma in GAME_TERM_FORMS.items():
        replaced = forms.pop(word, [])
        forms[word] = [lemma]
        # Drop entries only reachable through the wrong reading ("Object Request Broker").
        for wrong in replaced:
            if wrong != lemma and wrong not in GAME_TERMS and not any(wrong in v for v in forms.values()):
                glossary.pop(wrong, None)
    for tokens in sentences.values():
        for token in tokens:
            if token[0] in GAME_TERM_FORMS:
                token[1] = GAME_TERM_FORMS[token[0]]
                token[2] = GAME_TERMS[token[1]]["pos"]
    for lemma, sense in GAME_SENSES.items():
        nouns = [e for e in glossary.get(lemma, []) if e["pos"] == "noun"]
        if nouns and not any(sense in e["senses"] for e in nouns):
            nouns[0]["senses"] = [sense] + nouns[0]["senses"][: MAX_SENSES - 1]
            if "tags" in nouns[0]:
                nouns[0]["tags"] = [[]] + nouns[0]["tags"][: MAX_SENSES - 1]


def enrich(glossary, sentences, kaikki_path):
    """Adds sense tags, examples, related words and etymologies from Wiktionary to the existing glossary,
    without reparsing the game's text. Entries are matched by part of speech and shared senses."""
    fresh, _ = load_kaikki(kaikki_path, set(glossary), match_forms=False)
    fields = ("examples", "related", "etymology")
    for lemma, entries in glossary.items():
        candidates = fresh.get(lemma, [])
        for entry in entries:
            match = max((c for c in candidates if c["pos"] == entry["pos"]),
                        key=lambda c: len(set(c["senses"]) & set(entry["senses"])), default=None)
            if match is None or not set(match["senses"]) & set(entry["senses"]):
                continue
            for field in fields:
                entry.pop(field, None)
                if field in match:
                    entry[field] = match[field]
            if "example" in match:
                entry["example"] = match["example"]
            # Tags follow the entry's own senses, which may include a game sense Wiktionary lacks.
            tag_of = dict(zip(match["senses"], match.get("tags", [])))
            tags = [tag_of.get(sense, []) for sense in entry["senses"]]
            entry.pop("tags", None)
            if any(tags):
                entry["tags"] = tags
    rank_related(glossary, sentences)


def split_compound(word, known, depth=0):
    """'Kartenstapel' -> ['Karte', 'Stapel'] when both parts are known words (with linking letters)."""
    low = word.lower()
    for i in range(len(low) - 3, 2, -1):
        head = low[i:]
        head_word = head[:1].upper() + head[1:]
        if not (known(head_word) or known(head)):
            continue
        left = low[:i]
        for link in LINKING:
            if link and not left.endswith(link):
                continue
            stem = left[: len(left) - len(link)] if link else left
            if len(stem) < 3:
                continue
            # A noun, an adjective, or a verb stem ("Nachzieh-stapel" from "nachziehen").
            for candidate in (stem[:1].upper() + stem[1:], stem, stem[:1].upper() + stem[1:] + "e", stem + "en", stem + "n"):
                if known(candidate):
                    return [candidate, head_word if known(head_word) else head]
            # A separable verb's stem Wiktionary lacks: "nachzieh" = "nach" + "ziehen".
            for particle in sorted(PARTICLES, key=len, reverse=True):
                rest = stem[len(particle):]
                if stem.startswith(particle) and len(rest) >= 3 and (known(rest + "en") or known(rest + "n")):
                    return [particle, rest + "en" if known(rest + "en") else rest + "n", head_word if known(head_word) else head]
            if depth < 1:
                inner = split_compound(stem, known, depth + 1)
                if inner:
                    return inner + [head_word if known(head_word) else head]
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pck", help="path to SlayTheSpire2.pck")
    parser.add_argument("--kaikki", help="kaikki.org German extract (.jsonl or .jsonl.gz)")
    parser.add_argument("--model", default="de_core_news_md", help="spaCy German model")
    parser.add_argument("--game-terms-only", action="store_true",
                        help="only apply GAME_TERMS and GAME_SENSES to the existing output, without rebuilding")
    parser.add_argument("--enrich-only", action="store_true",
                        help="only add tags, examples, related words and etymologies from --kaikki to the existing "
                             "output, without reading the game or parsing")
    args = parser.parse_args()

    if args.enrich_only:
        if not args.kaikki:
            parser.error("--enrich-only needs --kaikki")
        glossary_data = json.loads((OUT_DIR / "glossary_deu_eng.json").read_text(encoding="utf-8"))
        sentence_data = json.loads((OUT_DIR / "sentences_deu.json").read_text(encoding="utf-8"))
        enrich(glossary_data["entries"], sentence_data["lines"], args.kaikki)
        write(OUT_DIR / "glossary_deu_eng.json", glossary_data)
        return

    if args.game_terms_only:
        glossary_data = json.loads((OUT_DIR / "glossary_deu_eng.json").read_text(encoding="utf-8"))
        sentence_data = json.loads((OUT_DIR / "sentences_deu.json").read_text(encoding="utf-8"))
        add_game_terms(glossary_data["entries"], glossary_data["forms"], sentence_data["lines"])
        write(OUT_DIR / "glossary_deu_eng.json", glossary_data)
        write(OUT_DIR / "sentences_deu.json", sentence_data)
        return
    if not (args.pck and args.kaikki):
        parser.error("--pck and --kaikki are required unless --game-terms-only is given")

    glossary, forms, compounds, sentences, report, counts = build(args.pck, args.kaikki, args.model)
    OUT_DIR.mkdir(parents=True, exist_ok=True)
    meta = {"version": 1, "source": "English Wiktionary via kaikki.org, CC BY-SA 4.0", "from": "deu", "to": "eng"}
    write(OUT_DIR / "glossary_deu_eng.json", {**meta, "entries": glossary, "forms": forms, "compounds": compounds})
    write(OUT_DIR / "sentences_deu.json", {"version": 1, "lines": sentences})
    print(json.dumps(report, indent=2))
    missing = [w for w, _ in counts.most_common() if w.lower() not in forms and w.lower() not in compounds
               and w not in glossary and w.lower() not in {g.lower() for g in glossary}]
    print("most frequent words without an entry:", ", ".join(missing[:40]))


def write(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":"), sort_keys=True), encoding="utf-8")
    print(f"wrote {path.relative_to(REPO)} ({path.stat().st_size / 1024:.0f} KB)")


if __name__ == "__main__":
    main()
