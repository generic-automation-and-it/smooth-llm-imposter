"""Offline workflow regression checks: python3 -m unittest discover -s .github/tests."""

import os
from pathlib import Path
import subprocess
import tempfile
import textwrap
import unittest


WORKFLOW = Path(__file__).resolve().parents[1] / "workflows/pipeline-ai-analyse.yml"


def step_script(name):
    lines = WORKFLOW.read_text().splitlines()
    start = lines.index(f"      - name: {name}") + 1
    end = next(
        (i for i in range(start, len(lines)) if lines[i].startswith("      - name:")),
        len(lines),
    )
    run = lines.index("        run: |", start, end) + 1
    return textwrap.dedent("\n".join(lines[run:end]))


class InitializationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.lib = self.root / "tooling/scripts/lib"
        self.lib.mkdir(parents=True)
        self.bin = self.root / "home/.local/bin"
        self.bin.mkdir(parents=True)
        self.env = {
            "PATH": str(self.bin) + os.pathsep + os.defpath,
            "HOME": str(self.root / "home"),
            "GITHUB_WORKSPACE": str(self.root),
            "GITHUB_ENV": str(self.root / "github-env"),
            "GITHUB_OUTPUT": str(self.root / "github-output"),
            "GITHUB_PATH": str(self.root / "github-path"),
            "TRACE": str(self.root / "trace"),
            "REVIEW_SKILL_DIR": str(self.root / "tooling"),
            "OPENCODE_CLI_VERSION": "2.0.29",
            "OPENCODE_REVIEW_REPORT_MODEL_PRIMARY": "review-primary",
            "OPENCODE_REVIEW_REPORT_MODEL_SECONDARY": "review-secondary",
            "OPENCODE_ANALYSE_MODEL": "analyse-model",
        }
        Path(self.env["GITHUB_ENV"]).touch()
        self.write_helper("resolve-provider.sh", """
            if [ "${OPENCODE_PROVIDER_SCOPE:-}" = analyse ]; then
              echo 'OPENCODE_ANALYSE_PROVIDER_ID=analyse-id' >> "$GITHUB_ENV"
            else
              echo 'OPENCODE_REVIEW_REPORT_PROVIDER_ID=review-id' >> "$GITHUB_ENV"
            fi
        """)
        self.write_helper("install-opencode.sh", r"""
            printf 'install:%s\n' "$OPENCODE_CLI_VERSION" >> "$TRACE"
        """)
        self.write_helper("prepare-opencode-config.sh", """
            export OPENCODE_CONFIG="$GITHUB_WORKSPACE/managed.json"
            echo "OPENCODE_CONFIG=$OPENCODE_CONFIG" >> "$GITHUB_ENV"
            echo prepare >> "$TRACE"
        """)
        self.write_helper("setup-opencode-config.sh", "exit 99")
        self.write_helper("ensure-opencode-models.sh", r"""
            printf 'models:%s|%s|%s|%s\n' "$1" "$2" "$3" "${4:-}" >> "$TRACE"
            exit "${REGISTRATION_RC:-0}"
        """)
        self.write_helper("quarantine-project-config.sh", """
            if [ "${1:-}" = --restore ]; then
              echo restore >> "$TRACE"
              mv "$GITHUB_WORKSPACE/original.json" "$GITHUB_WORKSPACE/opencode.json"
            else
              quarantine_project_config() {
                echo quarantine >> "$TRACE"
                mv "$1/opencode.json" "$1/original.json"
                echo managed > "$1/opencode.json"
              }
            fi
        """)
        self.write_helper("opencode-health.sh", """
            echo "health:${OPENCODE_CONFIG:-missing}" >> "$TRACE"
            exit "${HEALTH_RC:-0}"
        """)
        opencode = self.bin / "opencode"
        opencode.write_text('#!/bin/bash\necho "opencode:$*:${OPENCODE_CONFIG:-missing}" >> "$TRACE"\n')
        opencode.chmod(0o755)
        git = self.bin / "git"
        git.write_text('#!/bin/bash\n[ "$1" = status ] || exit 98\necho status >> "$TRACE"\n')
        git.chmod(0o755)
        curl = self.bin / "curl"
        curl.write_text('#!/bin/bash\necho "Network access is forbidden in workflow tests" >&2\nexit 97\n')
        curl.chmod(0o755)
        (self.root / "opencode.json").write_text("original")

    def write_helper(self, name, content):
        (self.lib / name).write_text("set -euo pipefail\n" + textwrap.dedent(content))

    def run_step(self, name, expected=0):
        result = subprocess.run(
            ["/bin/bash", "-c", step_script(name)],
            cwd=self.root, env=self.env, capture_output=True, text=True, timeout=10,
        )
        self.assertEqual(result.returncode, expected, result.stdout + result.stderr)
        # GitHub Actions starts a new shell per step and imports GITHUB_ENV.
        for line in Path(self.env["GITHUB_ENV"]).read_text().splitlines():
            key, value = line.split("=", 1)
            self.env[key] = value
        path_file = Path(self.env["GITHUB_PATH"])
        if path_file.exists():
            self.env["PATH"] = os.pathsep.join(path_file.read_text().splitlines() + [os.defpath])
        return result

    def initialize(self, expected=0):
        self.run_step("Resolve provider/model chain")
        return self.run_step("Initialize OPENCODE", expected)

    def trace(self):
        return Path(self.env["TRACE"]).read_text().splitlines()

    def test_current_tooling_preserves_pin_config_and_initialization_order(self):
        self.initialize()
        config = self.root / "managed.json"
        self.assertEqual(self.trace(), [
            "install:2.0.29", "prepare",
            f"models:{config}|review-id|review-primary|review-secondary",
            f"models:{config}|analyse-id|analyse-model|",
            "quarantine", f"opencode:service stop:{config}",
            f"opencode:stats:{config}", f"health:{config}",
        ])
        self.assertEqual(self.env["OPENCODE_CONFIG"], str(config))

    def test_legacy_config_helper_remains_supported(self):
        (self.lib / "prepare-opencode-config.sh").unlink()
        self.write_helper("setup-opencode-config.sh", 'echo legacy >> "$TRACE"')
        self.initialize()
        self.assertIn("legacy", self.trace())

    def test_optional_new_helpers_can_be_absent(self):
        (self.lib / "ensure-opencode-models.sh").unlink()
        (self.lib / "quarantine-project-config.sh").unlink()
        self.initialize()
        self.assertFalse(any(line.startswith("models:") for line in self.trace()))

    def test_blank_analyse_model_inherits_review_provider(self):
        self.env["OPENCODE_ANALYSE_MODEL"] = ""
        self.initialize()
        self.assertNotIn("OPENCODE_ANALYSE_PROVIDER_ID", self.env)
        self.assertEqual(sum(line.startswith("models:") for line in self.trace()), 1)

    def test_install_failure_stops_before_config_preparation(self):
        self.write_helper("install-opencode.sh", "exit 42")
        self.initialize(expected=42)
        self.assertFalse(Path(self.env["TRACE"]).exists())

    def test_config_failure_is_fatal_without_legacy_fallback(self):
        self.write_helper("prepare-opencode-config.sh", "return 43")
        self.initialize(expected=43)
        self.assertEqual(self.trace(), ["install:2.0.29"])

    def test_model_registration_failure_remains_advisory(self):
        self.env["REGISTRATION_RC"] = "1"
        self.initialize()
        self.assertTrue(self.trace()[-1].startswith("health:"))

    def test_wrong_service_config_binding_is_fatal(self):
        self.env["HEALTH_RC"] = "3"
        result = self.initialize(expected=1)
        self.assertIn("::error::", result.stdout)

    def test_other_health_failures_remain_advisory(self):
        for code in (1, 2):
            with self.subTest(code=code):
                self.env["HEALTH_RC"] = str(code)
                self.initialize()

    def test_original_project_config_is_restored_before_status_or_staging(self):
        self.initialize()
        self.assertEqual((self.root / "opencode.json").read_text().strip(), "managed")
        self.run_step("Commit fixes")
        self.assertEqual((self.root / "opencode.json").read_text(), "original")
        self.assertEqual(self.trace()[-2:], ["restore", "status"])

    def test_commit_step_accepts_tooling_without_quarantine_helper(self):
        (self.lib / "quarantine-project-config.sh").unlink()
        self.initialize()
        self.run_step("Commit fixes")
        self.assertEqual((self.root / "opencode.json").read_text(), "original")


if __name__ == "__main__":
    unittest.main()
