#!/bin/bash
set -e

# Script: find-context-files.sh
# Purpose: Discover relevant *AGENTS.md files based on changed files in PR
# Usage: Called from pipeline-code-review-report.yml workflow
# Output: ci_temp/context_files.txt with list of relevant context files

echo "Looking for relevant *AGENTS.md files based on changed files..."

# Check if we have changed files
if [ ! -f ci_temp/changed_files.txt ] || [ ! -s ci_temp/changed_files.txt ]; then
  echo "No changed files to analyze"
  echo "has_context=false" >> "$GITHUB_OUTPUT"
  echo "context_file_count=0" >> "$GITHUB_OUTPUT"
  exit 0
fi

# Extract unique directory paths from changed files
tr '\0' '\n' < ci_temp/changed_files.txt | while IFS= read -r file; do
  dirname "$file"
done | sort -u > ci_temp/changed_dirs.txt

# Filter: Keep only AGENT.md files in parent paths of changed files
> ci_temp/relevant_agents_files.txt
while IFS= read -r changed_dir; do
  # For each changed directory, walk up to root checking for AGENT.md files
  current_dir="$changed_dir"
  while [ "$current_dir" != "." ] && [ -n "$current_dir" ]; do
    # Check if any *AGENTS.md exists in current directory (excluding templates)
    find "$current_dir" -maxdepth 1 -type f -name "*AGENTS.md" ! -name "TEMPLATE_*" >> ci_temp/relevant_agents_files.txt 2>/dev/null || true
    # Move up one directory
    current_dir=$(dirname "$current_dir")
  done
done < ci_temp/changed_dirs.txt

# Note: Root-level AGENTS.md is handled via MANDATORY_CONTEXT_FILES
# Feature-specific *AGENTS.md files in root (e.g., CLAIMS_MIGRATION_TO_NEW_MODULE_AGENTS.md)
# should NOT be auto-included - they are only relevant when their feature area has changes

# Add mandatory context files (always loaded for all reviews)
# These are configured in the workflow via MANDATORY_CONTEXT_FILES env variable
echo "Adding mandatory context files..."
if [ -n "$MANDATORY_CONTEXT_FILES" ]; then
  for ctx_file in $MANDATORY_CONTEXT_FILES; do
    if [ -f "$ctx_file" ]; then
      echo "$ctx_file" >> ci_temp/relevant_agents_files.txt
      echo "  - $ctx_file (mandatory)"
    else
      echo "  ⚠ Warning: Mandatory context file not found: $ctx_file"
    fi
  done
else
  echo "  ⚠ Warning: MANDATORY_CONTEXT_FILES env variable not set"
fi

# Remove duplicates and sort
if [ -s ci_temp/relevant_agents_files.txt ]; then
  sort -u ci_temp/relevant_agents_files.txt > ci_temp/context_files.txt
  CONTEXT_FILE_COUNT=$(wc -l < ci_temp/context_files.txt | tr -d ' ')

  echo ""
  echo "Found $CONTEXT_FILE_COUNT relevant context files:"
  cat ci_temp/context_files.txt

  # No size filtering - the model reads files on-demand via its read tool.
  # File paths are listed in the prompt; opencode.json sets
  # permission.external_directory: allow (LADR-025) so reads of in-repo dot-paths
  # succeed in headless run mode (the old gemini-cli used --yolo for this).
  echo ""
  echo "Context file paths will be provided to the model for on-demand reading"
  echo "has_context=true" >> "$GITHUB_OUTPUT"
  echo "context_file_count=$CONTEXT_FILE_COUNT" >> "$GITHUB_OUTPUT"
else
  echo "No relevant context files found"
  echo "has_context=false" >> "$GITHUB_OUTPUT"
  echo "context_file_count=0" >> "$GITHUB_OUTPUT"
fi
