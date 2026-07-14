# Release checklist — the human-gate items

Automated build, tests, smokes and packaging remain CI gates. This checklist contains the decisions and interactions
that genuinely require a human. The frozen release surface and platform claims are in
[`docs/production-hardening.md`](docs/production-hardening.md) and
[`docs/supported-platforms.md`](docs/supported-platforms.md). The exact execution order is
[`docs/rc-acceptance.md`](docs/rc-acceptance.md).

> **Status 2026-07-14:** the 1.0.1 listing correction and five-target package set are being cut. Kane uploads the
> accepted packages through the Marketplace portal. Do not create a release tag or publish while any required
> automated gate is red or while a mandatory human acceptance step for a selected artifact is failed or incomplete.

## Phase 0 — before the first published build
- [x] **License — DECIDED + committed: Elastic License 2.0 (source-available)** (root `LICENSE`; the 2026-07-06
      ratified source-available call superseded the earlier MIT-core decision, and the paid value still lives behind
      the `apply_change_plan` gate, so a fork of the open core has nothing to unlock). The
      `"license": "Elastic-2.0"` field is now in `Semanticus.VSCode/package.json`.
- [ ] **Marketplace publisher ownership.** `Semanticus.VSCode/package.json` declares
      `"publisher": "semanticus-vscode"`. Confirm that exact id is registered, owned by the release account and
      accepted by Marketplace before the RC tag. Change the manifest only if Marketplace requires a different id,
      then rebuild and repeat package acceptance.
- [ ] **`VSCE_PAT` secret.** Create a Marketplace PAT; add it as the GitHub Actions secret `VSCE_PAT`
      (the dormant `.github/workflows/publish.yml` reads it on a `v*` tag).

## Phase 1 — unlock autonomous live-tenant verification (the big leverage)
- [x] **Service principal — WIRED + read-only Fabric REST lane LIVE-VERIFIED (Nexwave tenant).** The engine
      auth already reads `FABRIC_CLIENT`/`FABRIC_SECRET`/`FABRIC_TENANT` (or `AZURE_*`); `CicdSmoke` now runs a
      READ-ONLY live block gated on those env vars, and `ci.yml` passes the matching GitHub secrets to it. Confirmed
      live: SP auth, `list_workspaces` (8), `list_deployment_pipelines` (8), `get_pipeline_stages` (a real
      Dev→Test→Prod board), and the door-safe `.Error` contract on a Fabric 4xx.
- [x] **XMLA live model lane — VERIFIED (FrameworkTesting / SM_Observatory).** The SP CAN open a model live over
      XMLA. Confirmed read-only against the tenant: `open_live`, `run_dax`, `vertipaq_scan` (LIVE storage stats, 8
      tables), and `ai_readiness_scan_live` (DMV-cardinality rules, graded). Baked into `AirSmoke` as a READ-ONLY
      block gated on the SP + `SEMANTICUS_LIVE_XMLA`/`SEMANTICUS_LIVE_DB`; `ci.yml` passes them.
  - [ ] **To turn BOTH live lanes on in CI:** add GitHub repo/org **secrets** — `FABRIC_CLIENT`/`FABRIC_SECRET`/
        `FABRIC_TENANT` (the SP), plus `SEMANTICUS_LIVE_XMLA` (e.g. `powerbi://api.powerbi.com/v1.0/myorg/<workspace>`)
        + `SEMANTICUS_LIVE_DB` (the test semantic model). CI then runs both live blocks; unset → they skip (green).
- [x] **DAX-equivalence keystone — VERIFIED LIVE.** The prover that gates `apply_change_plan` discriminates
      correctly against the real live model: a results-changing rewrite (1→2) is SKIPPED by the verify gate; an
      equivalent one (1→1) applies. Asserted in the AirSmoke live block (read-only; the plan mutates the in-memory
      session only — no server write).
- [x] **`deploy_live` WRITE lane — supervised round-trip VERIFIED (and reverted).** A measure-description
      round-trip on SM_Observatory: clean baseline dry-run (0 changes) → edit → dry-run (1 change) → atomic COMMIT
      (`Model.SaveChanges`, Committed=true) → verified in the live model → REVERT → verified restored to original
      (no residue). Run as a SUPERVISED one-off (not a CI smoke — writes stay explicitly confirmed, never autorun).
  - [x] **Fabric ALM writes stay supervised.** Deployment-pipeline, Fabric Git, CI/CD publication and Data Agent
        publishing preview as a dry run and write only on explicit confirmation. Do not enable live writes merely
        to manufacture package evidence.

## Phase 3 — ship
- [ ] **Production VSIX build rail.** CI must build Windows x64, Windows ARM64, Linux x64, macOS Intel and macOS
      Apple Silicon on matching runners, allow-list and security-scan every finished archive, then extract and
      execute its bundled engine. The aggregate job must reproduce all five SHA-256 digests and provenance records.
- [ ] **Clean-machine install/upgrade/uninstall and first-engine-start pass.** Human-run T115 gate for every package
      selected for upload; automated archive execution does not substitute for VS Code installation and interaction.
- [ ] **RC human acceptance.** Execute [`docs/rc-acceptance.md`](docs/rc-acceptance.md) against the exact RC
      artifact and complete a copy of
      [`docs/release-evidence/rc-signoff-template.md`](docs/release-evidence/rc-signoff-template.md). A skip,
      ambiguity, failed live revert, missing independent security review, or open P0/P1 blocks release.
- [ ] **G7 RC performance.** On the registered Windows machine, run the exact baseline comparison in
      [`docs/performance-gate.md`](docs/performance-gate.md) against the RC commit and attach the JSON result. Repeat
      with the large-model fixture. Record extension-host startup during the F5 pass. Any p95 above 120 percent of
      its comparable baseline needs an investigated and explicitly accepted reason.
- [ ] **Final RC merge call.** Confirm the chosen SHA is on `origin/main`, all required CI jobs for that exact SHA
      are green, every security-sensitive PR has independent approval, and there are zero open P0/P1 defects.
- [x] **Version and release notes.** Version 1.0.1 is stamped in `package.json` and `package-lock.json`, with a
      matching CHANGELOG section for the listing corrections and platform coverage. A published Marketplace version
      is never reused.
- [ ] **Code-signing cert** for the `.vsix` (optional for Marketplace; required for some orgs).
- [ ] Complete the ordered **F5 interaction gate** in `docs/rc-acceptance.md` on the final RC build.

## Phase 4 — monetize
- [ ] **Paddle** account to live mode; product/price plus checkout/customer portal; verify the shipped
      Upgrade/Pro-options pathway (`LicenseEntitlement.ManageUrl`, currently `https://semanticus.com.au/pro`) resolves to
      the approved live Pro page; license issuer plus webhook.
- [ ] Ratify the published **free/paid line** + Terms/Privacy/refund.

## Known limitations

The public limitation set is maintained in [`docs/supported-platforms.md`](docs/supported-platforms.md). Technical
follow-ups remain in `TASKS.md` and `docs/PLAN.md`; they are not silently promoted into or removed from the frozen
release surface here.
