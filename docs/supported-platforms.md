# Semanticus 1.0.1 support and limitations

This is the public support contract for Semanticus 1.0.1. A package is accepted only when its bundled engine is
extracted and executed on a matching CI runner. CI does not replace the documented human clean-install and
live-environment acceptance.

## Platform support

| Platform | Status | Supported boundary |
|---|---|---|
| Windows 11 x64 | Supported release package | Platform-specific VSIX, bundled engine, offline BIM/PBIP/TMDL, Power BI Desktop discovery, local XMLA, local M preview, remote XMLA and Fabric features where tenant prerequisites are met |
| Windows 11 ARM64 | Supported release package | Platform-specific VSIX, bundled engine, offline BIM/PBIP/TMDL, remote XMLA and Fabric features where tenant prerequisites are met |
| Ubuntu 24.04 x64 | Supported release package | Platform-specific VSIX, bundled engine, offline BIM/PBIP/TMDL, remote XMLA and Fabric features where tenant prerequisites are met |
| macOS Intel | Supported release package | Platform-specific VSIX, bundled engine, offline BIM/PBIP/TMDL, remote XMLA and Fabric features where tenant prerequisites are met |
| macOS Apple Silicon | Supported release package | Platform-specific VSIX, bundled engine, offline BIM/PBIP/TMDL, remote XMLA and Fabric features where tenant prerequisites are met |

Windows support does not imply that every live feature works in every tenant. Remote XMLA and Fabric behavior needs
a compatible capacity, tenant setting, permission and identity. An unavailable capability must fail with an honest
error and must never be reported as a successful operation.

## Supported model boundary

- Offline BIM, PBIP and TMDL journeys are limited to formats and compatibility levels represented in the final
  acceptance corpus.
- Modern calendar metadata requires compatibility level 1701 or later. The classic Date-table path remains for
  older models until the user explicitly upgrades.
- Power BI Desktop discovery, local live connections and local M preview require Windows 11 x64.
- Unsupported metadata must fail closed and preserve the source model.

## Accepted 1.0 limitations

- Role and object security analysis is static-first. Semanticus does not impersonate a role or user to prove dynamic
  RLS behavior.
- Lineage cloud-report discovery is supported behind explicit one-time consent. Local PBIR report artifacts
  remain in scope.
- Fabric deployment-pipeline, Fabric Git and CI/CD publication writes preview as a dry run and apply only on
  explicit confirmation.
- Data Agent schema authoring and publishing are supported (publishing previews as a dry run and is Pro-gated);
  unresolved datasource identity remains deferred.
- The new `copilot/` tooling format is detected but not parsed until its on-disk schema is authoritative.
- Classic date tables are never silently rewritten into modern calendars.
- Multi-process multi-model instances, automatic workflow-profile selection and broad team collaboration are
  post-1.0 work.

## Human acceptance

The exact clean-install, F5, performance, live-write/revert and publication gates are in
[`rc-acceptance.md`](rc-acceptance.md). A skipped or ambiguous required gate is not a pass. The corresponding record
must name the RC SHA and the exact VSIX SHA-256.
