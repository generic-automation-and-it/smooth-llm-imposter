#!/usr/bin/env python3
"""Create a GitHub Task (sub-issue) from the current git diff vs main,
add it to the local GitHub Project, and link it as a sub-issue of a parent Feature issue.

Horizontal slicing: one task per technical layer (backend, tests, docs, ai-tooling, etc.)
Vertical slicing lives at the Feature (parent issue) level.
"""

import argparse
import json
import os
import subprocess
import sys


def run(cmd, check=True, capture=True):
    result = subprocess.run(cmd, check=check, text=True, capture_output=capture)
    return result.stdout.strip()


def run_json(cmd):
    result = subprocess.run(cmd, check=True, text=True, capture_output=True)
    return json.loads(result.stdout)


def ensure_tool(name):
    try:
        run([name, "--version"], check=True)
    except Exception:
        raise RuntimeError(f"Required tool not available: {name}")


def git(args):
    return run(["git"] + args)


def get_repo_info():
    """Detect owner/repo from the git remote URL."""
    remote = run(["git", "remote", "get-url", "origin"])
    if "github.com" not in remote:
        raise RuntimeError("Remote origin does not appear to be a GitHub repository.")
    # Normalise both https and ssh formats
    remote = remote.replace("git@github.com:", "https://github.com/").rstrip("/").removesuffix(".git")
    parts = remote.split("github.com/")[-1].split("/")
    if len(parts) >= 2:
        return parts[0], parts[1]
    raise RuntimeError(f"Could not parse owner/repo from remote URL: {remote}")


def get_base_ref(preferred_ref):
    if preferred_ref:
        base = git(["merge-base", "HEAD", preferred_ref])
        return base, preferred_ref

    for ref in ["origin/main", "main"]:
        try:
            base = git(["merge-base", "HEAD", ref])
            if base:
                return base, ref
        except subprocess.CalledProcessError:
            continue

    base = git(["rev-parse", "HEAD"])
    return base, "HEAD"


def parse_name_status(name_status):
    paths = []
    status_counts = {"A": 0, "M": 0, "D": 0, "R": 0}

    for line in name_status.splitlines():
        if not line.strip():
            continue
        parts = line.split("\t")
        status = parts[0]
        if status.startswith("R"):
            status_counts["R"] += 1
            paths.append(parts[2] if len(parts) >= 3 else parts[1] if len(parts) == 2 else "")
            continue
        if status in status_counts:
            status_counts[status] += 1
        if len(parts) >= 2:
            paths.append(parts[1])

    return [p for p in paths if p], status_counts


def classify_horizontal_slice(paths):
    """Return the horizontal technical layers touched by the diff."""
    layers = []
    if any(p.startswith(("src/", "Project")) for p in paths):
        layers.append("backend")
    if any("test" in p.lower() or "spec" in p.lower() for p in paths):
        layers.append("tests")
    if any(p.endswith(".md") or p.startswith((".docs/", "docs/")) for p in paths):
        layers.append("documentation")
    if any(p.startswith((".agents/", ".github/")) for p in paths):
        layers.append("ai-tooling")
    if any(p.endswith((".json", ".yml", ".yaml", ".env", ".ps1", ".sh")) for p in paths):
        layers.append("config")
    if not layers:
        layers.append("general")
    return layers


def summarize_areas(paths, max_items=6):
    areas = []
    for path in paths:
        area = path.split("/")[0]
        if area and area not in areas:
            areas.append(area)
    if not areas:
        return ["repo"]
    if len(areas) > max_items:
        return areas[:max_items] + [f"+{len(areas) - max_items} more"]
    return areas


def truncate_lines(text, max_lines=60, max_chars=4000):
    lines = text.splitlines()
    if len(lines) > max_lines:
        lines = lines[:max_lines] + ["... (truncated)"]
    truncated = "\n".join(lines)
    if len(truncated) > max_chars:
        truncated = truncated[:max_chars] + "\n... (truncated)"
    return truncated


def build_title(areas, layers, branch_name):
    layer_tag = " / ".join(layers)
    if len(areas) == 1:
        return f"[{layer_tag}] Update {areas[0]}"
    if len(areas) == 2:
        return f"[{layer_tag}] Update {areas[0]} and {areas[1]}"
    return f"[{layer_tag}] Update {areas[0]} and related areas"


def build_task_body(branch_name, base_ref, base_sha, paths, status_counts, diff_stat, layers, feature_issue):
    layer_str = ", ".join(f"`{layer}`" for layer in layers)
    areas = summarize_areas(paths)
    area_str = ", ".join(f"`{a}`" for a in areas)

    checklist = ["- [ ] Diff reviewed and scope confirmed against parent Feature."]
    if any(p.startswith(("src/", "Project")) for p in paths):
        checklist.append("- [ ] Build succeeds or follow-up issue raised.")
    if any("test" in p.lower() or "spec" in p.lower() for p in paths):
        checklist.append("- [ ] Tests updated and passing, or follow-up recorded.")
    if any(p.endswith(".md") for p in paths):
        checklist.append("- [ ] Documentation updates verified.")
    if status_counts.get("D", 0) > 0:
        checklist.append("- [ ] Deleted assets reviewed for downstream impact.")
    if feature_issue:
        checklist.append(f"- [ ] Task linked as sub-issue of Feature #{feature_issue} in project.")

    changes_str = (
        f"Added: {status_counts.get('A', 0)}, "
        f"Modified: {status_counts.get('M', 0)}, "
        f"Deleted: {status_counts.get('D', 0)}, "
        f"Renamed: {status_counts.get('R', 0)}"
    )
    feature_ref = f"#{feature_issue}" if feature_issue else "_not specified_"

    body = f"""\
## Task

> **Horizontally sliced task** generated from branch `{branch_name}` diff. Parent feature: {feature_ref}.

## Context

| Field | Value |
|-------|-------|
| Branch | `{branch_name}` |
| Base | `{base_ref}` (`{base_sha[:7]}`) |
| Layers | {layer_str} |
| Areas | {area_str} |
| Files changed | {len(paths)} ({changes_str}) |

## Diff Summary

```
{diff_stat}
```

## Acceptance Criteria

{chr(10).join(checklist)}
"""
    return body.strip()


LAYER_TO_TYPE = {
    "documentation": "docs",
    "tests": "test",
    "config": "chore",
    "ai-tooling": "chore",
    "backend": "feat",
    "general": "chore",
}


def suggest_branch_name(layers, title, issue_number):
    """Derive a <type>/<issue>-short-description branch name (git-policy convention)."""
    branch_type = LAYER_TO_TYPE.get(layers[0], "chore")
    # Slug from the title: drop the "[layer]" prefix, keep alphanumerics, hyphenate
    slug_source = title.split("]", 1)[-1] if title.startswith("[") else title
    words = "".join(c if c.isalnum() else " " for c in slug_source.lower()).split()
    slug = "-".join(words[:6]) or "update"
    return f"{branch_type}/{issue_number}-{slug}"


def link_sub_issue(owner, repo, parent_issue_number, child_issue_number):
    """Link child issue as a sub-issue of the parent using the GitHub REST API."""
    try:
        run_json([
            "gh", "api", "--method", "POST",
            f"/repos/{owner}/{repo}/issues/{parent_issue_number}/sub_issues",
            "-f", f"sub_issue_id={child_issue_number}",
        ])
        return True
    except Exception:
        return False


def parse_feature_issue(value):
    """Accept an issue number (int or str) or a GitHub issue URL and return the int issue number."""
    if not value:
        return 0
    value = str(value).strip()
    # Full URL: https://github.com/owner/repo/issues/42
    if value.startswith("http"):
        parts = value.rstrip("/").split("/")
        try:
            return int(parts[-1])
        except (ValueError, IndexError):
            raise RuntimeError(f"Could not parse issue number from URL: {value}")
    # Plain number
    try:
        return int(value)
    except ValueError:
        raise RuntimeError(f"--feature-issue must be an integer or GitHub issue URL, got: {value}")


def main():
    parser = argparse.ArgumentParser(
        description=(
            "Create a GitHub Task (sub-issue) from the git diff vs main, "
            "add it to the GitHub Project, and optionally link to a parent Feature."
        )
    )
    parser.add_argument(
        "--feature-issue",
        help=(
            "Parent Feature issue — accepts a GitHub issue number (e.g. 42) "
            "or a full issue URL (e.g. https://github.com/org/repo/issues/42). "
            "When set, the task is linked as a sub-issue of this Feature."
        ),
    )
    parser.add_argument("--title", help="Override task title.")
    parser.add_argument(
        "--repo",
        help="GitHub repo as owner/repo. Auto-detected from git remote when omitted.",
    )
    parser.add_argument(
        "--project", type=int, default=1,
        help="GitHub project number under the org (default: 1).",
    )
    parser.add_argument(
        "--no-project", action="store_true",
        help="Create the issue only; do not add it to any GitHub Project.",
    )
    parser.add_argument(
        "--org",
        help="GitHub org that owns the project. Defaults to the repo owner detected from the git remote.",
    )
    parser.add_argument(
        "--label", default="task",
        help="Label to apply to the created issue (default: task).",
    )
    parser.add_argument(
        "--base-ref",
        help="Override base ref (default: origin/main, then main).",
    )
    parser.add_argument(
        "--dry-run", action="store_true",
        help="Print output without creating anything.",
    )
    parser.add_argument(
        "--open", action="store_true",
        help="Open the created issue in the browser.",
    )

    args = parser.parse_args()

    ensure_tool("git")
    ensure_tool("gh")

    base_sha, base_ref = get_base_ref(args.base_ref)
    branch_name = git(["rev-parse", "--abbrev-ref", "HEAD"])

    if args.repo:
        owner, repo = args.repo.split("/", 1)
    else:
        owner, repo = get_repo_info()

    # Default the project org to the repo owner when not explicitly provided.
    org = args.org or owner

    name_status = git(["diff", "--name-status", f"{base_sha}..HEAD"])
    paths, status_counts = parse_name_status(name_status)

    if not paths:
        print("No changes detected against base ref. Nothing to create.", file=sys.stderr)
        return 0

    areas = summarize_areas(paths)
    layers = classify_horizontal_slice(paths)

    diff_stat = truncate_lines(git(["diff", "--stat", f"{base_sha}..HEAD"]))

    feature_issue = parse_feature_issue(args.feature_issue)
    title = args.title or build_title(areas, layers, branch_name)
    body = build_task_body(branch_name, base_ref, base_sha, paths, status_counts, diff_stat, layers, feature_issue)

    if args.dry_run:
        print(f"Title:\n{title}\n")
        print(f"Body:\n{body}\n")
        if args.no_project:
            print(f"Would create issue in {owner}/{repo} (no project).")
        else:
            print(f"Would create issue in {owner}/{repo} and add to project {org}/projects/{args.project}.")
        if feature_issue:
            print(f"Would link as sub-issue of Feature #{feature_issue}.")
        return 0

    # Create the issue
    create_cmd = [
        "gh", "issue", "create",
        "--repo", f"{owner}/{repo}",
        "--title", title,
        "--body", body,
    ]
    if args.label:
        create_cmd += ["--label", args.label]

    issue_url = run(create_cmd)
    issue_number = int(issue_url.rstrip("/").split("/")[-1])
    print(f"Created task issue #{issue_number}: {issue_url}")
    print(
        f"Suggested branch rename (git-policy convention): "
        f"git branch -m {suggest_branch_name(layers, title, issue_number)}"
    )

    # Add to GitHub Project (unless suppressed)
    if args.no_project:
        print("Skipped project add (--no-project).")
    else:
        try:
            run([
                "gh", "project", "item-add", str(args.project),
                "--owner", org,
                "--url", issue_url,
            ])
            print(f"Added to project {org}/projects/{args.project}.")
        except Exception as exc:
            print(f"Warning: could not add to project: {exc}", file=sys.stderr)

    # Link as sub-issue of the Feature
    if feature_issue:
        if link_sub_issue(owner, repo, feature_issue, issue_number):
            print(f"Linked as sub-issue of Feature #{feature_issue}.")
        else:
            print(
                f"Note: sub-issue API link failed. "
                f"Manually add #{issue_number} as a sub-issue of #{feature_issue}.",
                file=sys.stderr,
            )

    if args.open:
        run(
            ["gh", "issue", "view", str(issue_number), "--repo", f"{owner}/{repo}", "--web"],
            capture=False,
        )

    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        print(f"Error: {exc}", file=sys.stderr)
        sys.exit(1)
