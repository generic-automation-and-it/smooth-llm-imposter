# Collaboration

## Contributing

- Work on a branch off `main`: `<type>/<ticket>-short-description` (e.g. `feat/15-add-dialect-prefix`).
- Commits and PR titles follow [Conventional Commits](https://www.conventionalcommits.org). See
  [`.agents/rules/git/`](.agents/rules/git/).
- Every PR should create or update at least one nested `AGENTS.md` context file.

Build, run, and test instructions: [Developers — Getting Started](.docs/wiki/developers-gettingstarted.md).

## Recommended standard AI tooling

Install the standard skills, rules, and hooks used for development from
[smooth-devex-template — install skills, rules and hooks into a brownfield repository](https://github.com/generic-automation-and-it/smooth-devex-template#install-skills-rules-and-hooks-into-a-brownfield-repository).
Use the global installation so shared tooling is maintained upstream rather than copied back into this repository.

### macOS / Linux

```bash
curl -fsSL https://raw.githubusercontent.com/generic-automation-and-it/smooth-devex-template/main/scripts/install-skills.sh | bash -s -- -r -s -h -d -g
```

### Windows (PowerShell)

```powershell
$script = [scriptblock]::Create((irm https://raw.githubusercontent.com/generic-automation-and-it/smooth-devex-template/main/scripts/install-skills.ps1)); & $script -r -s -h -d -g
```

These commands download and execute upstream scripts; review the installer before running it.
`-r`, `-s`, and `-h` select rules, skills, and hooks; `-d` opts into AI decision validation;
`-g` installs globally in the user profile rather than vendoring into the working tree.
The installer supplies its upstream-owned set, not every historical skill copied into this repository.
See the linked guide for supported harnesses, configuration, updates, and uninstall options.

## Repository-owned skills

SmoothLlmImposter maintains these router-specific skills:

- [`imposter-newsession`](.agents/skills/imposter-newsession/) — mint a synthetic upstream session mapping.
- [`imposter-who`](.agents/skills/imposter-who/) — inspect the resolved upstream model and session.

Ownership and mirror-maintenance rules live in [`.agents/skills/AGENTS.md`](.agents/skills/AGENTS.md).
Usage and marketplace installation: [README — Skills](README.md#skills).
