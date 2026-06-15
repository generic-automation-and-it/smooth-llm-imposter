#!/usr/bin/env bash

set -u

build_configuration="${BUILD_CONFIGURATION:-Release}"
artifacts_root="${ARTIFACTS_ROOT:-artifacts}"
wiremock_url="${WIREMOCK_URL:-http://127.0.0.1:19091}"
timeout_seconds="${DEPENDENCY_TIMEOUT_SECONDS:-120}"
results_directory="${artifacts_root}/testresults"
coverage_directory="${artifacts_root}/coverage"
coverage_collect="XPlat Code Coverage;Format=cobertura;Include=[SmoothLlmImposter.*]*;ExcludeByFile=**/*.g.cs,**/obj/**"

wait_for_wiremock() {
  local elapsed=0

  echo "Waiting for WireMock health at ${wiremock_url}/__admin/health..."
  while ! curl -sf --max-time 2 "${wiremock_url}/__admin/health" > /dev/null 2>&1; do
    sleep 2
    elapsed=$((elapsed + 2))

    if [ "${elapsed}" -ge "${timeout_seconds}" ]; then
      echo "ERROR: Timed out after ${timeout_seconds}s waiting for WireMock at ${wiremock_url}"
      return 1
    fi
  done

  echo "WireMock is healthy at ${wiremock_url} (${elapsed}s)"
}

wait_for_wiremock || exit 1

dotnet tool restore || exit 1

rm -rf "${results_directory}" "${coverage_directory}"
mkdir -p "${results_directory}" "${coverage_directory}"

echo "Running test suite (SmoothLlmImposter.slnx)..."
dotnet test SmoothLlmImposter.slnx \
  --configuration "${build_configuration}" \
  --no-build \
  --results-directory "${results_directory}" \
  "--collect:${coverage_collect}" \
  || exit 1

dotnet tool run reportgenerator \
  "-reports:${results_directory}/**/coverage.cobertura.xml" \
  "-targetdir:${coverage_directory}" \
  "-reporttypes:HtmlInline_AzurePipelines;Cobertura;TextSummary;MarkdownSummaryGithub" \
  || exit 1
