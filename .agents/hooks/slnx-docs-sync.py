"""
Claude Code PostToolUse hook – slnx-docs-sync
Mirrors the OpenCode plugin at .opencode/plugins/slnx-docs-sync.ts.

Keeps *.slnx in sync when NFR or ADR documents are created
or edited by a Write/Edit/NotebookEdit tool call.

Receives a JSON payload on stdin from Claude Code:
  {
    "tool_name": "Write" | "Edit" | ...,
    "tool_input": { "file_path": "<absolute path>" },
    ...
  }
"""

import glob
import json
import os
import re
import sys


def main() -> None:
    try:
        data = json.load(sys.stdin)
    except json.JSONDecodeError:
        sys.exit(0)

    tool_input = data.get("tool_input", {})
    file_path: str = tool_input.get("file_path", "")
    if not file_path:
        sys.exit(0)

    # Claude Code hooks run with cwd = project root.
    repo_root = os.getcwd()

    try:
        rel = os.path.relpath(file_path, repo_root).replace("\\", "/")
    except ValueError:
        # Paths on different drives — skip.
        sys.exit(0)

    # Map path prefix to the matching .slnx solution folder.
    if rel.startswith(".docs/adr/"):
        folder_name = "/.docs/adr/"
    elif rel.startswith(".docs/nfr/"):
        folder_name = "/.docs/nfr/"
    else:
        sys.exit(0)

    slnx_files = glob.glob(os.path.join(repo_root, "*.slnx"))
    if not slnx_files:
        sys.exit(0)
    slnx_path = slnx_files[0]
    try:
        content = open(slnx_path, encoding="utf-8").read()
    except OSError:
        sys.exit(0)

    file_entry = f'<File Path="{rel}" />'

    # Already present — nothing to do.
    if file_entry in content:
        sys.exit(0)

    # Locate the target <Folder> element. It may be a container
    # (<Folder Name="X"> … </Folder>) or, when the folder is currently
    # empty, a self-closing element (<Folder Name="X" />).
    open_prefix = f'<Folder Name="{folder_name}"'
    folder_idx = content.find(open_prefix)
    if folder_idx == -1:
        sys.exit(0)

    tag_end = content.find(">", folder_idx)
    if tag_end == -1:
        sys.exit(0)

    # Indentation of the <Folder> line; nested entries get two more spaces.
    folder_line_start = content.rfind("\n", 0, folder_idx) + 1
    fm = re.match(r"^(\s*)", content[folder_line_start:folder_idx])
    folder_indent = fm.group(1) if fm else ""
    entry_indent = folder_indent + "  "

    if content[tag_end - 1] == "/":
        # Self-closing: expand into a container holding the new file.
        block = (
            f'<Folder Name="{folder_name}">\n'
            f"{entry_indent}{file_entry}\n"
            f"{folder_indent}</Folder>"
        )
        updated = content[:folder_idx] + block + content[tag_end + 1 :]
    else:
        # Container: insert before the matching </Folder>.
        closing_idx = content.find("</Folder>", tag_end)
        if closing_idx == -1:
            sys.exit(0)
        updated = (
            content[:closing_idx]
            + f"{entry_indent}{file_entry}\n"
            + content[closing_idx:]
        )
    open(slnx_path, "w", encoding="utf-8").write(updated)
    print(f"slnx-docs-sync: added {rel} → {folder_name}", file=sys.stderr)


if __name__ == "__main__":
    main()
