#!/usr/bin/env python3
"""Re-read Google's prose documentation and report where it no longer says what
spec/v4/verified-facts.json records.

Discovery is machine-readable and already diffed weekly. The per-method pages are
not, and they are where the semantic overrides get their evidence: two scope
defects reached a release because nothing re-read them. This closes that gap.

It reports and exits non-zero on drift. It never edits anything, because the
right response to a difference may be to change the SDK, to change the manifest,
or to record a new conflict — and only a person can tell which.

    python eng/check-verified-facts.py [--version v4]
"""
from __future__ import annotations

import argparse
import html
import json
import pathlib
import re
import sys
import urllib.error
import urllib.request

SCOPE = re.compile(r"https://www\.googleapis\.com/auth/[A-Za-z0-9_.\-]+")

# Enough of a browser to be served the same page a reader gets.
HEADERS = {"User-Agent": "Mozilla/5.0 (compatible; health-data-dotnet spec check)"}


def fetch(url: str) -> str:
    request = urllib.request.Request(url, headers=HEADERS)

    with urllib.request.urlopen(request, timeout=60) as response:  # noqa: S310 - fixed https URLs
        raw = response.read().decode("utf-8", "replace")

    # Scripts carry unrelated URLs and would be read as scopes.
    raw = re.sub(r"(?is)<(script|style)\b.*?</\1>", " ", raw)
    return re.sub(r"\s+", " ", html.unescape(re.sub(r"(?s)<[^>]+>", " ", raw)))


def check_method(entry: dict) -> list[str]:
    problems: list[str] = []
    text = fetch(entry["url"])
    found = set(SCOPE.findall(text))

    if not found:
        # Far more likely to mean the check broke than that Google removed authorization.
        return [f"{entry['operation']}: no scope URL found on the page at all — "
                f"treat as unreadable rather than as a change ({entry['url']})"]

    expected = set(entry["scopes"])

    for scope in sorted(found - expected):
        problems.append(f"{entry['operation']}: the page now lists {scope}, which the manifest does not")

    for scope in sorted(expected - found):
        problems.append(f"{entry['operation']}: the page no longer lists {scope}")

    for phrase in entry.get("mustContain", []):
        if phrase.lower() not in text.lower():
            problems.append(f"{entry['operation']}: the page no longer contains \"{phrase}\"")

    return problems


def check_guide(entry: dict) -> list[str]:
    problems: list[str] = []
    text = fetch(entry["url"])

    for phrase in entry["mustContain"]:
        if phrase.lower() not in text.lower():
            problems.append(f"{entry['id']}: \"{phrase}\" is no longer on {entry['url']}")

    return problems


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", default="v4")
    arguments = parser.parse_args()

    root = pathlib.Path(__file__).resolve().parent.parent
    manifest_path = root / "spec" / arguments.version / "verified-facts.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))

    print(f"Checking {manifest_path.relative_to(root).as_posix()}, "
          f"recorded {manifest['verifiedOn']}")

    problems: list[str] = []
    checked = 0

    for entry in manifest["methods"]:
        checked += 1
        try:
            problems += check_method(entry)
        except (urllib.error.URLError, TimeoutError) as error:
            problems.append(f"{entry['operation']}: could not read {entry['url']} ({error})")

    for entry in manifest["guides"]:
        checked += 1
        try:
            problems += check_guide(entry)
        except (urllib.error.URLError, TimeoutError) as error:
            problems.append(f"{entry['id']}: could not read {entry['url']} ({error})")

    print(f"Checked {checked} page(s).")

    if not problems:
        print("No drift: every recorded fact is still stated by its source.")
        return 0

    print(f"\n{len(problems)} difference(s):\n")
    for problem in problems:
        print(f"  - {problem}")

    print("\nDecide what changed before editing anything. A scope appearing may mean the SDK")
    print("should accept it; a scope disappearing rarely means it should be dropped, because")
    print("these pages have been observed omitting scopes the service still accepts.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
