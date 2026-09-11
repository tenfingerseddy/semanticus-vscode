#requires -Version 7.0
<#
Gate T163 (kane-gate-pack block 8): "the AI Assistant can never make a sign-in window
appear". Replaces most of a 25-minute hands-on block.

WHAT IT ACTUALLY CERTIFIES, and the limit that shapes everything below.

The agent-door refusals are proven in-process, by the xUnit suite, and do not depend on
watching anything: each chokepoint refuses at credential build with "Interactive
authentication is needed". That part is solid.

The extra claim this script exists to make is the one a human used to make with his eyes:
that no sign-in surface appeared on the desktop. tools/release/signin-sentinel.ps1 watches
for it. But if a browser of the same family was ALREADY RUNNING when the watch began, a
sign-in opened as a BACKGROUND TAB creates no new top-level window and no new process
family, so the watch cannot see it. Ignoring browser child-process churn is unavoidable
(an idle Edge spawns children constantly) and it is precisely what makes the hole.

So this script does NOT certify PASS on a machine with a browser open. It reports VOID,
with the reason and the fix (close the browser and run it again). VOID is a real outcome
and it is honest; PASS with that blind spot would be worse than the human it replaces,
because a person watching his own screen would have seen the tab.

  verdict PASS  the agent door refused, the desktop was watchable, nothing appeared,
                and the control proved the watcher can see a sign-in surface
  verdict VOID  nothing failed, but the run cannot support the claim (browser was
                already running, or the watcher was never armed in time)
  verdict FAIL  something actually went wrong

Usage:
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir>

THE SHIPPED ARTIFACT. The in-process runs above build Semanticus.Tests from the checkout,
so on their own they certify a Debug build from source that nobody runs, and this repo has
already been bitten by a stale Release DLL silently truncating the MCP tool list, so Debug
versus Release and from-source versus packaged are known to differ here in ways that matter.
So the gate also PACKAGES the .vsix locally (the same Semanticus.VSCode/scripts/package.mjs
that CI runs), extracts the Release engine out of it, and drives THAT binary out of process
over the real MCP stdio door: tools/release/packaged-engine-probe.mjs. It is packaged locally
and never downloaded from a CI run, because a release gate that depends on a green CI run
depends on the very thing it exists to certify.

That claim is deliberately NARROW, and the gate prints its limits rather than hiding them:
the packaged run covers the ENGINE BINARY and the NO-RECORD branch only. It does not cover
the packaged EXTENSION, nor the record-present or stale-cache branches, which need the two
internal in-process seams. Widening it by adding production seams is the wrong direction and
is its own scheduled item. The artifact is recorded by digest and version in block8.json,
because a certificate that does not name what it certified is not a certificate.

Self-tests. Each proves the gate can still say no, and ALL ALWAYS EXIT NON-ZERO:
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir> -ProveCanFail
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir> -SimulateDeadControl
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir> -SimulateBlindObserver
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir> -ProvePackagedCanFail
  pwsh tools/release/run-block8.ps1 -EvidenceDir <dir> -SimulateBlindStore

  exit 0  normal mode, genuine PASS. NOTHING ELSE EVER EXITS 0.
  exit 1  a non-PASS verdict. In a self-test this is the CORRECT result.
  exit 2  the self-test itself failed: the gate certified PASS when it was rigged not to.
          Exit 2 means this gate can no longer be trusted to fail. Treat it as a P1.

Stale evidence is REFUSED, not reused: a leftover stop or baseline file from an earlier run
can make a watch look armed when no sentinel is alive, which is exactly how a self-test
comes out green. Each run writes to its own timestamped directory and re-checks.

SAFETY. The suite isolates BOTH halves of the auth store (the record JSON via
EntraToken.PersistDirOverride and the encrypted MSAL cache via
EntraToken.TokenCacheNameOverride), so the real records and the real token cache are never
read or written. The written procedure it replaces DELETED the real records and restored
them afterwards.

Run it on a quiet machine, with the browser closed. That is not politeness: with the
browser closed the gate can certify, and with it open the gate cannot.
#>
param(
    [Parameter(Mandatory)][string]$EvidenceDir,
    [switch]$ProveCanFail,
    [switch]$SimulateDeadControl,
    [switch]$SimulateBlindObserver,
    [switch]$ProvePackagedCanFail,
    [switch]$SimulateBlindStore,
    # Escape hatch for a host whose packaging target cannot be built or run here. It DOWNGRADES the
    # claim rather than skipping quietly: the run can no longer reach PASS, only VOID.
    [switch]$SkipPackagedEngine,
    [int]$NegativeWatchSeconds = 6,
    [int]$PositiveWatchSeconds = 20,
    [int]$ArmTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path "$PSScriptRoot\..\..").Path
$sentinel = Join-Path $PSScriptRoot 'signin-sentinel.ps1'
$testProj = Join-Path $repo 'Semanticus.Tests\Semanticus.Tests.csproj'
$filterAll = 'FullyQualifiedName~AgentSigninObservedTests'
$filterPos = 'FullyQualifiedName~Human_browser_lane_really_raises_a_signin_surface_positive_control'
$selfTest = $ProveCanFail -or $SimulateDeadControl -or $SimulateBlindObserver -or $ProvePackagedCanFail -or $SimulateBlindStore
# The smallest number of polls a watch of a few seconds must have completed. A watch that
# polled once or twice did not meaningfully observe the interval it claims to cover.
$minPolls = 5

New-Item -ItemType Directory -Force -Path $EvidenceDir | Out-Null
# A unique root per invocation. Reusing one directory is how a dead sentinel's leftover
# baseline/stop files get mistaken for a live watch.
$runRoot = Join-Path $EvidenceDir ('t163\run-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path $runRoot) { throw "run directory already exists, refusing to reuse it: $runRoot" }
New-Item -ItemType Directory -Force -Path $runRoot | Out-Null

# One shared build, so neither watched interval contains compiler processes.
Write-Host '== building the test project (outside both watches) =='
& dotnet build $testProj -v quiet --nologo 2>&1 | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw "build failed with exit code $LASTEXITCODE" }

function Invoke-WatchedRun {
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Filter,
        [Parameter(Mandatory)][int]$WatchSeconds,
        # The thing to do while the desktop is watched. It receives the run's output directory and must
        # return an exit code. Defaults to the xUnit filter run, which is what the two original runs do.
        [scriptblock]$Action,
        [switch]$PositiveOptIn,
        [switch]$Blind
    )
    $out = Join-Path $runRoot $Name
    if (Test-Path $out) { throw "stale run directory: $out" }
    New-Item -ItemType Directory -Force -Path $out | Out-Null
    foreach ($stale in 'stop', 'baseline.json', 'armed.json', 'summary.json') {
        if (Test-Path (Join-Path $out $stale)) { throw "stale $stale in a fresh run directory: $out" }
    }

    $sentinelArgs = @('-NoProfile', '-File', $sentinel, '-OutDir', $out)
    if ($Blind) { $sentinelArgs += '-SelfTestBlind' }
    $proc = Start-Process pwsh -PassThru -WindowStyle Hidden -ArgumentList $sentinelArgs

    # Wait for a COMPLETED first poll, not merely for a baseline. baseline.json proves only
    # that a snapshot was taken; armed.json is written at the end of the first poll, so it is
    # the first moment a new surface could actually be detected. Anything raised before this
    # is invisible, so the action must not start until it exists.
    $deadline = (Get-Date).AddSeconds($ArmTimeoutSeconds)
    while (-not (Test-Path "$out\armed.json") -and (Get-Date) -lt $deadline) {
        if ($proc.HasExited) { throw "the sentinel exited before arming (exit $($proc.ExitCode)); see $out" }
        Start-Sleep -Milliseconds 25
    }
    $armed = Test-Path "$out\armed.json"

    if ($PositiveOptIn) { $env:SEMANTICUS_T163_POSITIVE = '1' } else { $env:SEMANTICUS_T163_POSITIVE = $null }
    try {
        if ($Action) {
            $testExit = & $Action $out
        }
        else {
            & dotnet test $testProj --no-build --nologo --filter $Filter 2>&1 |
                Tee-Object (Join-Path $out 'test.log') | Out-Null
            $testExit = $LASTEXITCODE
        }
    }
    finally { $env:SEMANTICUS_T163_POSITIVE = $null }

    Start-Sleep -Seconds $WatchSeconds
    New-Item -ItemType File -Force -Path "$out\stop" | Out-Null
    $proc.WaitForExit(30000) | Out-Null

    if (-not (Test-Path "$out\summary.json")) { throw "the sentinel wrote no summary; see $out" }
    $s = Get-Content "$out\summary.json" -Raw | ConvertFrom-Json
    [pscustomobject]@{
        name = $Name; dir = $out; testExit = $testExit; summary = $s; armed = $armed
        verdict = $s.verdict; claimStrength = $s.claimStrength
        browserLaneObservable = [bool]$s.browserLaneObservable
    }
}

# ---- the SHIPPED artifact ----------------------------------------------------------------------
# Everything above and below this block runs an in-process LocalEngine compiled from the checkout,
# which certified a build nobody runs. The probe below drives the engine binary out of a packaged
# .vsix instead. Packaging happens HERE, outside every watch, because a self-contained publish
# spawns hundreds of processes and would flood a watched interval.
#
# Packaged LOCALLY on purpose, never downloaded from a CI run: a release gate that depends on a
# green CI run depends on the very thing it exists to certify.
$probe = Join-Path $PSScriptRoot 'packaged-engine-probe.mjs'
$vsixPath = $null
$packagingEvidence = $null
$packagingMatched = $false
if (-not $SkipPackagedEngine) {
    Write-Host ''
    Write-Host '== packaging the shipped .vsix (outside every watch) =='
    $pkgOut = Join-Path $runRoot 'packaged-artifact'
    New-Item -ItemType Directory -Force -Path $pkgOut | Out-Null
    $target = (& node -p 'process.platform + "-" + process.arch').Trim()
    if ($LASTEXITCODE -ne 0) { throw 'could not determine the packaging target' }
    $extension = Join-Path $repo 'Semanticus.VSCode'
    $version = (Get-Content (Join-Path $extension 'package.json') -Raw | ConvertFrom-Json).version
    $produced = Join-Path $extension "dist/semanticus-$target-$version.vsix"
    # Observe a fresh output, not a successful command plus a path supplied by the probe.
    # The expected archive must be absent before invoking the real source packager.
    if (Test-Path -LiteralPath $produced) { Remove-Item -LiteralPath $produced -Force }
    & node (Join-Path $extension 'scripts/package.mjs') $target 2>&1 | Tee-Object (Join-Path $pkgOut 'package.log') | Select-Object -Last 2
    if ($LASTEXITCODE -ne 0) { throw "packaging the .vsix failed with exit code $LASTEXITCODE; see $pkgOut/package.log" }
    if (-not (Test-Path -LiteralPath $produced -PathType Leaf)) { throw "packaging produced no fresh .vsix: $produced" }
    # Keep the exact output this run observed, independent of later writes to dist/.
    $vsixPath = Join-Path (Resolve-Path $pkgOut).Path (Split-Path $produced -Leaf)
    Copy-Item -LiteralPath $produced -Destination $vsixPath
    $packagingEvidence = [pscustomobject]@{
        path = $vsixPath
        sha256 = (Get-FileHash -LiteralPath $vsixPath -Algorithm SHA256).Hash.ToLowerInvariant()
        bytes = (Get-Item -LiteralPath $vsixPath).Length
        target = $target
        version = $version
    }
}

Write-Host ''
Write-Host '== NEGATIVE: the agent door must refuse, and nothing may appear =='
if ($SimulateBlindObserver) {
    # MUST 8 self-test: blind the observer and see whether anything notices. Without the
    # liveness checks below, a blind watch produces a spotless NOTHING_APPEARED and certifies.
    Write-Host '   (-SimulateBlindObserver: the window enumerator is disabled on purpose)'
}
$neg = Invoke-WatchedRun -Name 'negative-agent-door' -Filter $filterAll -WatchSeconds $NegativeWatchSeconds -Blind:$SimulateBlindObserver

# The SHIPPED-ARTIFACT run, watched like the others: the packaged engine must refuse over the real
# MCP stdio door AND raise nothing on the desktop while it does.
$pkg = $null
if (-not $SkipPackagedEngine) {
    Write-Host ''
    Write-Host '== SHIPPED ARTIFACT: the packaged engine must refuse, out of process =='
    $probeArgs = @($probe, '--vsix', $vsixPath)
    if ($ProvePackagedCanFail) {
        Write-Host '   (-ProvePackagedCanFail: demanding the shipped engine NOT refuse, which it always does)'
        $probeArgs += '--prove-can-fail'
    }
    if ($SimulateBlindStore) {
        Write-Host '   (-SimulateBlindStore: blinding the auth-store snapshot on purpose)'
        $probeArgs += '--simulate-blind-store'
    }
    $pkg = Invoke-WatchedRun -Name 'shipped-packaged-engine' -WatchSeconds $NegativeWatchSeconds -Action {
        param($out)
        & node @probeArgs --out $out 2>&1 | Tee-Object (Join-Path $out 'probe.log') | Out-Null
        return $LASTEXITCODE
    }
    $pkgJsonPath = Join-Path $pkg.dir 'packaged-engine-probe.json'
    if (-not (Test-Path $pkgJsonPath)) { throw "the packaged-engine probe wrote no result; see $($pkg.dir)\probe.log" }
    $pkgJson = Get-Content $pkgJsonPath -Raw | ConvertFrom-Json
    $packagingMatched = $pkgJson.artifact.vsix -ceq (Split-Path $packagingEvidence.path -Leaf) -and
        $pkgJson.artifact.vsixSha256 -ceq $packagingEvidence.sha256 -and
        $pkgJson.artifact.vsixBytes -eq $packagingEvidence.bytes -and
        $pkgJson.artifact.target -ceq $packagingEvidence.target -and
        $pkgJson.artifact.version -ceq $packagingEvidence.version -and
        (Get-FileHash -LiteralPath $vsixPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $packagingEvidence.sha256
}

Write-Host ''
Write-Host '== POSITIVE CONTROL: the human door must really raise a sign-in surface =='
if ($SimulateDeadControl) {
    # Self-test of the fake-pass guard: withhold the opt-in so the control test self-skips and
    # raises nothing. The negative run is clean, so a gate without this guard would certify a
    # confident PASS on evidence that proves nothing.
    Write-Host '   (-SimulateDeadControl: running the control WITHOUT its opt-in, so it raises nothing)'
    $pos = Invoke-WatchedRun -Name 'positive-control-human-door' -Filter $filterPos -WatchSeconds 3
} else {
    $pos = Invoke-WatchedRun -Name 'positive-control-human-door' -Filter $filterPos -WatchSeconds $PositiveWatchSeconds -PositiveOptIn
}

# ---- adjudicate -------------------------------------------------------------------
$failures = [System.Collections.Generic.List[string]]::new()
$voids    = [System.Collections.Generic.List[string]]::new()

if ($neg.testExit -ne 0) { $failures.Add("the agent-door suite did not pass (dotnet test exit $($neg.testExit)); see $($neg.dir)\test.log") }
# LIVENESS OF THE OBSERVER ITSELF. #289 found that the scanner which fails builds for
# tests that cannot fail could itself not fail: break one function and all four of its own
# checks stayed green. This gate is the same shape, so ask the same question: if the part
# that DOES the observing silently stopped, would anything fail? Without these two checks the
# answer was no, because a blind watch yields a spotless NOTHING_APPEARED.
if ([int]$neg.summary.baselineWindowCount -le 0) {
    $failures.Add("the observer saw ZERO windows at baseline, which cannot be true of a real desktop: it was not observing, so NOTHING_APPEARED means nothing. (selfTestBlind=$($neg.summary.selfTestBlind))")
}
if ([int]$neg.summary.polls -lt $minPolls) {
    $failures.Add("the observer completed only $($neg.summary.polls) polls (minimum $minPolls), so it cannot be said to have watched the run.")
}
if ($neg.verdict -ne 'NOTHING_APPEARED') {
    $hardKinds = @('SIGNIN_TITLE_MATCH', 'NEW_WINDOW_IN_WATCHED_PROCESS', 'NEW_WATCHED_PROCESS')
    $who = @($neg.summary.items | Where-Object { $_.kind -in $hardKinds } | ForEach-Object { "$($_.kind):$($_.process)" }) -join ', '
    $failures.Add("a sign-in surface appeared during the agent-door run: $who. That is a release blocker.")
}

# THE CONTROL. It is only a control if it caught a surface a sign-in would produce, so it
# requires a SIGNIN_TITLE_MATCH specifically: any new browser process, any unrelated
# auth-broker event or any browser title change would otherwise certify it. And a control
# test that FAILED cannot vouch for anything, so its exit code counts too.
$controlSignin = [int]$pos.summary.signinDetections -gt 0
$controlTestOk = $pos.testExit -eq 0
$controlFired  = $controlSignin -and $controlTestOk

if (-not $selfTest) {
    if (-not $controlTestOk) {
        $failures.Add("the positive-control test itself failed (dotnet test exit $($pos.testExit)), so it cannot vouch for the watcher.")
    }
    if (-not $controlSignin) {
        $failures.Add("the positive control produced no SIGNIN_TITLE_MATCH (verdict $($pos.verdict), sign-in matches $($pos.summary.signinDetections)). The watcher cannot be shown to detect a sign-in surface, so the negative run proves NOTHING.")
    }
}

# THE SHIPPED ARTIFACT. Without this the whole gate certified a from-source Debug build, which is not
# what any user runs — and this repo has already been bitten by a stale Release DLL silently
# truncating the MCP tool list, so the two are known to differ here.
if ($SkipPackagedEngine) {
    $voids.Add("-SkipPackagedEngine was set, so the packaged .vsix was never probed and this run says nothing about the artifact we ship.")
}
else {
    if (-not $packagingMatched) {
        $failures.Add('the probed artifact does not match the fresh archive independently observed during packaging; no local-packaging claim is supported.')
    }
    if (-not $pkgJson.certified) {
        $why = if ($pkgJson.failures) { ($pkgJson.failures -join ' | ') } else { 'no reason recorded' }
        $failures.Add("the PACKAGED engine did not certify: $why")
    }
    # The packaged run is watched like every other. A surface raised by the SHIPPED binary is the
    # worst outcome the gate can find, so it is judged exactly as harshly as the in-process run.
    if ($pkg.verdict -ne 'NOTHING_APPEARED') {
        $hardKinds = @('SIGNIN_TITLE_MATCH', 'NEW_WINDOW_IN_WATCHED_PROCESS', 'NEW_WATCHED_PROCESS')
        $who = @($pkg.summary.items | Where-Object { $_.kind -in $hardKinds } | ForEach-Object { "$($_.kind):$($_.process)" }) -join ', '
        $failures.Add("a sign-in surface appeared while the PACKAGED engine ran: $who. That is a release blocker.")
    }
    if ([int]$pkg.summary.baselineWindowCount -le 0) {
        $failures.Add("the observer saw ZERO windows at baseline during the packaged-engine run, so it was not observing.")
    }
}

# -ProveCanFail rigs the gate by demanding something known to be false: that the agent-door
# run DID raise a sign-in surface. The agent door refuses, so it never does. If this ever
# stops producing a failure, the adjudication has stopped reading the negative run at all.
if ($ProveCanFail -and $neg.verdict -ne 'SOMETHING_APPEARED') {
    $failures.Add("-ProveCanFail: demanded SOMETHING_APPEARED from the agent-door run, which refuses and therefore raises nothing. The gate said no, which is the point of this mode.")
}

# Anything that makes the run unable to support the claim, as opposed to failing it.
if (-not $neg.armed) { $voids.Add("the watcher never confirmed it was armed within $ArmTimeoutSeconds s, so the start of the agent-door run was unobserved.") }
if (-not $neg.browserLaneObservable) {
    $voids.Add("a browser was already running at baseline ($($neg.summary.browsersRunningAtBaseline -join ', ')), so a sign-in opened as a BACKGROUND TAB would have created no new window and no new process family and could not have been seen. Close the browser and run it again.")
}

$verdict =
    if ($failures.Count -gt 0) { 'FAIL' }
    elseif (-not $controlFired) { 'VOID' }
    elseif ($voids.Count -gt 0) { 'VOID' }
    else { 'PASS' }

$reason =
    if ($verdict -eq 'FAIL') { $failures -join ' | ' }
    elseif ($verdict -eq 'VOID') {
        (@($voids) + @(if (-not $controlFired) { "the positive control did not fire (sign-in matches $($pos.summary.signinDetections), control test exit $($pos.testExit)), so nothing here licenses a claim about the browser lane." })) -join ' | '
    }
    else {
        "The agent door refused on every chokepoint. The desktop was watchable (no browser at baseline), the watcher was armed $($neg.summary.baselineToArmedMs)ms in and polled $($neg.summary.polls) times at a $($neg.summary.pollGapMsMean)ms mean gap, and nothing appeared. The control raised a surface the same watcher caught ($($pos.summary.signinDetections) sign-in title match(es))."
    }

$unproven = @(
    # THE SCOPE OF THE SHIPPED-ARTIFACT CLAIM. It is narrow on purpose. A narrow claim that names its
    # own limits is worth more than a broad one that hides them.
    'The packaged run exercises the ENGINE BINARY only, out of the .vsix. The TypeScript extension host, the webview and the activation path are NOT run, so nothing here certifies the packaged EXTENSION.',
    'The packaged run reaches the NO-RECORD branch only: it refuses because no saved auth record matches its unregistered client id. The record-present and stale-cache branches need the two internal in-process seams and are covered in-process only. They were deliberately NOT widened by adding production seams.',
    'A .vsix is not byte-reproducible: repackaging identical source yields a different digest, because the archive carries timestamps. The recorded sha256 identifies WHICH artifact was probed; it is not a fingerprint of the source that built it.',
    'The agent-door refusals happen at credential build, so no request reaches Entra or Fabric. Nothing here tests a real network round trip.',
    "A surface that lived and died inside one poll gap could be missed: mean $($neg.summary.pollGapMsMean)ms, worst $($neg.summary.pollGapMsMax)ms on this run.",
    "The window between taking the baseline and the first completed poll is unobserved: $($neg.summary.baselineToArmedMs)ms on this run.",
    'A BACKGROUND TAB in an already-running browser is undetectable by this method. It is the reason a run with a browser open reports VOID instead of PASS, and it is not fixed by watching titles, because a background tab changes no top-level title either.',
    'A hidden, minimised or zero-size window is not distinguished from an absent one beyond its title and owning process.',
    'A surface on a different desktop or on the secure desktop cannot be enumerated by EnumWindows at all, so it would never be seen.',
    'A console prompt involves no window. Only the device-code callback is separately pinned in-process (AgentNonInteractiveTests); any other console-based prompt would be invisible here.',
    'The browser and broker process list is a heuristic and cannot be exhaustive: an unlisted Chromium build would neither count as evidence nor be spotted at baseline, so it would also inflate claimStrength.',
    'The sign-in title pattern is a heuristic: a localised or renamed sign-in surface could miss it, leaving only process attribution.',
    'Tool windows are demoted to IGNORED_TOOL_WINDOW unless their title matches a sign-in, and unrelated-process windows are recorded but never counted. Both are auditable in the evidence, and both are judgement calls.',
    'prepare_working_copy is NOT exercised by this run. Its agent-origin rule is covered offline by AgentNonInteractiveDoorTests; a fake connection id would fail for the wrong reason and prove nothing.',
    'The wrong-person refusal (two accounts on one tenant) is NOT exercised by this run; it is covered offline by AgentNonInteractiveTests.',
    'The RPC-door deploy_gate path is NOT RUN, here or anywhere. It shares the code this proves, but sharing code is not the same as exercising it.',
    'The control uses a client id that is not a registered application, so the flow cannot complete. That is asserted by observing that no token and no auth record were persisted, NOT guaranteed by the id itself: if that id ever became a real registration the control would need re-checking.',
    'Window titles are redacted in the kept evidence, so a reader cannot independently re-identify which surface appeared; only its classification survives.'
)

$result = [pscustomobject]@{
    gate      = 'T163 agent non-interactive full surface (block 8)'
    verdict   = $verdict
    reason    = $reason
    mode      = if ($ProveCanFail) { 'prove-can-fail (demands an outcome the agent door cannot produce)' }
                elseif ($SimulateDeadControl) { 'simulate-dead-control (fake-pass guard self-test)' }
                elseif ($SimulateBlindObserver) { 'simulate-blind-observer (observer-liveness self-test)' }
                else { 'normal' }
    claimStrength         = $neg.claimStrength
    browserLaneObservable = $neg.browserLaneObservable
    controlFired          = $controlFired
    controlSigninMatches  = [int]$pos.summary.signinDetections
    controlTestExit       = $pos.testExit
    negative  = [pscustomobject]@{
        verdict = $neg.verdict; testExit = $neg.testExit; armed = $neg.armed
        polls = $neg.summary.polls; intervalMsRequested = $neg.summary.intervalMsRequested
        baselineWindowCount = $neg.summary.baselineWindowCount; selfTestBlind = $neg.summary.selfTestBlind
        pollGapMsMean = $neg.summary.pollGapMsMean; pollGapMsMax = $neg.summary.pollGapMsMax
        baselineToArmedMs = $neg.summary.baselineToArmedMs
        hardDetections = $neg.summary.hardDetections; churnIgnored = $neg.summary.churnIgnored
        unrelatedIgnored = $neg.summary.unrelatedIgnored; toolWindowIgnored = $neg.summary.toolWindowIgnored
    }
    positive  = [pscustomobject]@{
        verdict = $pos.verdict; testExit = $pos.testExit; armed = $pos.armed
        signinDetections = $pos.summary.signinDetections; hardDetections = $pos.summary.hardDetections
    }
    # WHAT WAS CERTIFIED, named by digest. A certificate that does not name its artifact is not a
    # certificate: two .vsix files with the same version differ whenever the source does.
    shippedArtifact = if ($SkipPackagedEngine) {
        [pscustomobject]@{ probed = $false; reason = '-SkipPackagedEngine was set' }
    }
    else {
        [pscustomobject]@{
            probed = $true
            certified = [bool]$pkgJson.certified -and $packagingMatched
            claim = if ($packagingMatched) { $pkgJson.claim } else { 'NOTHING IS CERTIFIED: packaging provenance did not match.' }
            vsix = $pkgJson.artifact.vsix
            vsixSha256 = $pkgJson.artifact.vsixSha256
            vsixBytes = $pkgJson.artifact.vsixBytes
            version = $pkgJson.artifact.version
            target = $pkgJson.artifact.target
            engineSha256 = $pkgJson.artifact.engineSha256
            engineBytes = $pkgJson.artifact.engineBytes
            # The probe cannot supply its own provenance. Bind its result to the parent's
            # fresh output observation and retain that evidence, not just the boolean.
            packagedLocally = $packagingMatched
            packagedByThisRunFromSource = $packagingMatched
            packagingEvidence = $packagingEvidence
            packagedByProbeItself = [bool]$pkgJson.artifact.packagedByThisProbe
            refused = [bool]$pkgJson.observed.refused
            authStoreFilesHashed = [int]$pkgJson.observed.authStoreFilesHashed
            authStoreChanged = @($pkgJson.observed.authStoreChanged)
            desktopVerdict = $pkg.verdict
            notCovered = @($pkgJson.notCovered)
        }
    }
    unproven  = $unproven
    evidence  = @($neg.dir, $pos.dir)
    recordedUtc = (Get-Date).ToUniversalTime().ToString('o')
}

$resultPath = Join-Path $EvidenceDir 'block8.json'
$result | ConvertTo-Json -Depth 6 | Set-Content $resultPath

Write-Host ''
Write-Host '================ BLOCK 8 ================'
Write-Host "verdict         : $verdict"
Write-Host "mode            : $($result.mode)"
Write-Host "negative        : $($neg.verdict) (tests exit $($neg.testExit), armed=$($neg.armed))"
Write-Host "positive ctrl   : $($pos.verdict) (sign-in matches $($pos.summary.signinDetections), tests exit $($pos.testExit)) -> fired=$controlFired"
if ($SkipPackagedEngine) {
    Write-Host "shipped artifact: NOT PROBED (-SkipPackagedEngine) - this run says nothing about what we ship"
}
else {
    Write-Host "shipped artifact: $($pkgJson.artifact.vsix) v$($pkgJson.artifact.version) sha256 $($pkgJson.artifact.vsixSha256.Substring(0,16))..."
    Write-Host "  packaged      : matches this run's observed source package=$packagingMatched, engine sha256 $($pkgJson.artifact.engineSha256.Substring(0,16))..."
    Write-Host "  out of process: certified=$($pkgJson.certified) refused=$($pkgJson.observed.refused) desktop=$($pkg.verdict) authStore=$(if (@($pkgJson.observed.authStoreChanged).Count) { 'MUTATED' } else { "unchanged ($($pkgJson.observed.authStoreFilesHashed) files hashed)" })"
    Write-Host '  NOT covered   : the packaged extension; the record-present and stale-cache branches'
}
Write-Host "browser lane    : observable=$($neg.browserLaneObservable) ($($neg.claimStrength))"
Write-Host "polls           : $($neg.summary.polls) @ $($neg.summary.pollGapMsMean)ms mean / $($neg.summary.pollGapMsMax)ms max, armed at $($neg.summary.baselineToArmedMs)ms"
Write-Host "result          : $resultPath"
if ($failures.Count) { Write-Host ''; Write-Host 'FAILURES:'; foreach ($f in $failures) { Write-Host "  - $f" } }
if ($voids.Count -or -not $controlFired) { Write-Host ''; Write-Host 'CANNOT CERTIFY:'; foreach ($v in $voids) { Write-Host "  - $v" }; if (-not $controlFired) { Write-Host '  - the positive control did not fire' } }
Write-Host '========================================='

# A self-test is rigged so the gate MUST refuse to certify. If it certified anyway, the gate
# has lost the ability to fail, which is worse than any single failing check.
if ($selfTest) {
    if ($verdict -eq 'PASS') {
        Write-Host ''
        Write-Host 'SELF-TEST BROKEN: the gate reported PASS while rigged to fail. Do not trust this gate.'
        exit 2
    }
    Write-Host ''
    Write-Host "self-test OK: the gate refused to certify (verdict $verdict), which is the expected result."
    exit 1
}

if ($verdict -ne 'PASS') { exit 1 }
exit 0
