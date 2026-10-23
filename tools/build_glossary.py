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

def load_kaikki(path, wanted):
    """Reads the kaikki.org German extract, keeping entries for words the game uses (any case)."""
    wanted_lower = {w.lower() for w in wanted}
    entries = defaultdict(list)   # lemma -> [entry]
    form_of = defaultdict(set)    # form (lower) -> lemmas
    opener = gzip.open if str(path).endswith(".gz") else open
    with opener(path, "rt", encoding="utf-8") as f:
        for line in f:
            item = json.loads(line)
            word = item.get("word", "")
            if item.get("lang_code") != "de" or not word:
                continue
            forms = [x.get("form", "") for x in item.get("forms", []) if x.get("form")]
            relevant = word.lower() in wanted_lower or any(x.lower() in wanted_lower for x in forms)
            if not relevant:
                continue
            senses, lemma_targets = [], set()
            for sense in item.get("senses", []):
                targets = sense.get("form_of") or sense.get("alt_of")
                if targets:
                    lemma_targets.update(t["word"] for t in targets if t.get("word"))
                    continue
                gloss = (sense.get("glosses") or [""])[0].strip()
                if gloss and gloss not in senses:
                    senses.append(gloss)
            for target in lemma_targets:
                form_of[word.lower()].add(target)
            if not senses:
                continue
            pos = POS.get(item.get("pos", ""), item.get("pos", ""))
            entry = {"pos": pos, "senses": senses[:MAX_SENSES]}
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
            example = first_example(item)
            if example:
                entry["example"] = example
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


def first_example(item):
    for sense in item.get("senses", []):
        for example in sense.get("examples", []):
            text, english = example.get("text", ""), example.get("english") or example.get("translation")
            if text and english and len(text) < 120:
                return {"de": text, "en": english}
    return None


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
    docs = list(nlp.pipe(samples.values(), batch_size=256))
    print(f"parsed {len(docs)} lines")

    surface_counts = Counter()
    for doc in docs:
        surface_counts.update(t.text for t in doc if WORD.fullmatch(t.text))
    wanted = set(surface_counts) | {t.lemma_ for doc in docs for t in doc if WORD.fullmatch(t.lemma_)}

    entries, form_of = load_kaikki(kaikki_path, wanted)
    print(f"kept {len(entries)} Wiktionary lemmas")
    lower_entries = defaultdict(list)
    for lemma in entries:
        lower_entries[lemma.lower()].append(lemma)

    def known(word):
        return word in entries or word.lower() in lower_entries

    def lemma_for(token):
        """Wiktionary lemma for a token, preferring spaCy's lemma when Wiktionary has it."""
        candidates = [token.lemma_, token.text]
        if token.pos_ in ("NOUN", "PROPN"):
            candidates.append(token.lemma_[:1].upper() + token.lemma_[1:])
        else:
            candidates.append(token.lemma_.lower())
        for candidate in candidates:
            if candidate in entries:
                return candidate
        for target in sorted(form_of.get(token.text.lower(), [])):
            if target in entries:
                return target
        for candidate in candidates:
            if candidate.lower() in lower_entries:
                return lower_entries[candidate.lower()][0]
        return token.lemma_

    sentences, verbs_used, compounds = {}, set(), {}
    for (key, _), doc in zip(samples.items(), docs):
        tokens, index_of = [], {}
        for t in doc:
            if WORD.fullmatch(t.text):
                index_of[t.i] = len(tokens)
                tokens.append([t.text.lower(), lemma_for(t), SPACY_POS.get(t.pos_, t.pos_.lower())])
        # Separable verbs: the particle (dependency "svp") belongs to its head verb.
        for t in doc:
            if t.dep_ == "svp" and t.i in index_of and t.head.i in index_of:
                verb = t.text.lower() + lemma_for(t.head).lower()
                v, p = index_of[t.head.i], index_of[t.i]
                tokens[v][1] = tokens[p][1] = verb
                tokens[v][2] = "verb"
                tokens[p][2] = "particle"
                tokens[v].append(p)
                tokens[p].append(v)
                verbs_used.add(verb)
        sentences[key] = tokens

    # Words with no entry of their own: try splitting compounds into known parts.
    for word in surface_counts:
        if known(word) or word.lower() in form_of or len(word) < 7:
            continue
        parts = split_compound(word, known)
        if parts:
            compounds[word.lower()] = parts

    used_lemmas = {tok[1] for tokens in sentences.values() for tok in tokens} | verbs_used
    used_lemmas |= {p for parts in compounds.values() for p in parts}
    glossary = {}
    for lemma in sorted(used_lemmas):
        for candidate in [lemma] + lower_entries.get(lemma.lower(), []):
            if candidate in entries:
                glossary[candidate] = entries[candidate]
                break

    forms = {}
    for word in surface_counts:
        low = word.lower()
        targets = sorted(t for t in form_of.get(low, []) if t in glossary)
        if targets and low not in {g.lower() for g in targets}:
            forms[low] = targets[:3]

    coverage = sum(1 for w in surface_counts if known(w) or w.lower() in form_of or w.lower() in compounds)
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
            for candidate in (stem[:1].upper() + stem[1:], stem, stem + "e", (stem + "e")[:1].upper() + stem[1:] + "e"):
                if known(candidate):
                    return [candidate, head_word if known(head_word) else head]
            if depth < 1:
                inner = split_compound(stem, known, depth + 1)
                if inner:
                    return inner + [head_word if known(head_word) else head]
    return None


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--pck", required=True, help="path to SlayTheSpire2.pck")
    parser.add_argument("--kaikki", required=True, help="kaikki.org German extract (.jsonl or .jsonl.gz)")
    parser.add_argument("--model", default="de_core_news_md", help="spaCy German model")
    args = parser.parse_args()

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
