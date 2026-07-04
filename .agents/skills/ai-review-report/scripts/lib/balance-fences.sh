#!/bin/bash
# Usage: source this file, then `balance_fences <file>` (in-place).
#
# Closes any code fence left open at the end of a model-generated markdown file.
#
# Why: the posted review embeds raw model output inside a <details> wrapper.
# Models sometimes NEST code fences (an outer ``` block containing ```yaml
# snippets). Same-length fences do not nest in GFM — the first inner bare ```
# closes the outer block and every intended "close" after it OPENS a new block,
# flipping fence parity for the entire rest of the document. When the parity
# ends "open", the <details>/<summary> wrapper (and everything else) is
# swallowed into a literal code block and the collapsible section stops
# folding (PR #36, review 4474042824). Balancing each model-generated piece
# before assembly makes the imbalance unable to leak past the piece boundary.
#
# GFM rules implemented (CommonMark §119-147, fenced code blocks):
# - A fence opens on 0-3 spaces of indent + 3 or more backticks or tildes,
#   plus an optional info string. A backtick fence's info string must not
#   contain a backtick (so inline code like ```foo``` never opens a block).
# - A fence closes only on a bare fence: same character, at least the opening
#   length, nothing but trailing spaces after it. A "closing" fence carrying
#   an info string (```yaml) does NOT close — it is literal content.
balance_fences() {
  local f="$1"
  [ -f "$f" ] || return 0
  awk '
    function fence_run(s, ch,    n) {
      n = 0
      while (substr(s, n + 1, 1) == ch) n++
      return n
    }
    BEGIN { open = 0 }
    {
      line = $0
      # 0-3 leading spaces allowed; 4+ is an indented code line, never a fence.
      pos = match(line, /[^ ]/)
      if (pos >= 1 && pos <= 4) {
        s = substr(line, pos)
        c = substr(s, 1, 1)
        if (open == 0) {
          if (c == "`" || c == "~") {
            n = fence_run(s, c)
            info = substr(s, n + 1)
            if (n >= 3 && !(c == "`" && info ~ /`/)) {
              open = 1; fchar = c; flen = n
            }
          }
        } else if (c == fchar) {
          n = fence_run(s, c)
          rest = substr(s, n + 1)
          gsub(/[ \t]/, "", rest)
          if (n >= flen && rest == "") open = 0
        }
      }
      print $0
    }
    END {
      if (open == 1) {
        out = ""
        for (i = 0; i < flen; i++) out = out fchar
        print out
      }
    }
  ' "$f" > "${f}.fencebal.tmp" && mv "${f}.fencebal.tmp" "$f"
}

if [[ "${BASH_SOURCE[0]}" == "${0}" ]]; then
  echo "This script is meant to be sourced, not executed directly." >&2
  exit 1
fi
