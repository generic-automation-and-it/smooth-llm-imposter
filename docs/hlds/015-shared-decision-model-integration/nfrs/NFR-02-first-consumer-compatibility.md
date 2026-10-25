# NFR-02: First-consumer compatibility

**Status:** Accepted

## Requirement

Extraction produces no observable regression in HLD 014's operator settings or tool-selection contract. Existing flat/structured defaults and precedence resolve identically. Disabled and no-drop bodies retain their original instance; successful pruning, ordering, history/choice protection and partial fail-open outcomes remain equivalent. No default configuration activates a consumer.

## Verification

Compare pre-extraction and shared-integration behavior using HLD 014's configuration, API golden, failure, cancellation and downgrade fixtures. Verify startup validation when all consumers are disabled and when tool filtering is enabled. Exercise affected configuration and decision calls on published native artifacts; preserve Linux AMD64/ARM64 native-runner gates.

Defaults and conventional precedence resolve identically, as asserted by `DecisionsToolsOptionsTests`: `Defaults_keep_filter_disabled_and_target_the_container_host`, `Structured_section_binds_every_decision_setting` and `All_conventional_variables_override_the_bound_section` each assert all ten settings; `Absent_or_blank_variables_preserve_bound_settings` covers all ten variables; `Every_decision_setting_has_a_conventional_variable_that_overrides_it` reflects over `DecisionsToolsOptions` so a new setting without a conventional variable fails closed.

## Acceptance Criteria

- Every operator setting HLD 014 documents keeps its documented default and effective value, including conventional-over-structured precedence.
- Existing tool-filter output and no-call/byte-identity assertions pass unchanged.
- Classifier extraction does not change inference routing or introduce endpoints.
- Native startup and decision-call behavior match the JIT contract; absent platform evidence is reported, not inferred.

## Applies To

Configuration resolution and HLD 014 integration; Goal 3.
