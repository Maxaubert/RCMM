#!/usr/bin/env python3
"""Generate manager/src/RCMM.Core/Resources/lucide-icons.tsv from a Lucide checkout.

Lucide is ISC-licensed. To refresh the bundled icon set:

    git clone --depth 1 --filter=blob:none --sparse https://github.com/lucide-icons/lucide.git /tmp/lucide
    cd /tmp/lucide && git sparse-checkout set icons
    python3 tools/gen-lucide-icons.py /tmp/lucide/icons manager/src/RCMM.Core/Resources/lucide-icons.tsv

Each output line is TAB-separated:  name <TAB> svg-fragment <TAB> categories(csv) <TAB> search-terms(csv)
  - svg-fragment  : the children of <svg> (rect/path/circle/...), whitespace-collapsed to one line.
  - categories    : Lucide's own category metadata (used for the picker's group headers).
  - search-terms  : tags + alias names (so "bin" finds trash-2, "code-square" finds square-code).

Deprecated icons (deprecated:true) are skipped — they're near-duplicate aliases of current names.
"""
import os, json, re, sys

def main():
    icons_dir = sys.argv[1]
    out_path = sys.argv[2]
    names = sorted(n[:-4] for n in os.listdir(icons_dir) if n.endswith(".svg"))
    rows, skipped = [], 0
    for name in names:
        with open(os.path.join(icons_dir, name + ".svg"), encoding="utf-8") as f:
            svg = f.read()
        meta = {}
        jpath = os.path.join(icons_dir, name + ".json")
        if os.path.exists(jpath):
            with open(jpath, encoding="utf-8") as f:
                meta = json.load(f)
        if meta.get("deprecated") is True:
            skipped += 1
            continue
        # Inner fragment: drop the opening <svg ...> and closing </svg>, collapse whitespace.
        inner = re.sub(r"^.*?<svg[^>]*>", "", svg, count=1, flags=re.S)
        inner = re.sub(r"</svg>\s*$", "", inner, flags=re.S)
        inner = re.sub(r"\s+", " ", inner).strip().replace(" />", "/>")
        cats = meta.get("categories") or []
        tags = list(meta.get("tags") or [])
        for a in (meta.get("aliases") or []):
            if isinstance(a, str):
                tags.append(a)
            elif isinstance(a, dict) and a.get("name"):
                tags.append(a["name"])
        if "\t" in inner or "\n" in inner:
            raise ValueError(f"{name}: fragment contains a tab/newline")
        rows.append("\t".join([name, inner, ",".join(cats), ",".join(tags)]))
    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(rows) + "\n")
    print(f"wrote {len(rows)} icons (skipped {skipped} deprecated) -> {out_path}")

if __name__ == "__main__":
    main()
