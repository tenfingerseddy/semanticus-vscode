#requires -Version 7.0
<#
Screen sentinel for gate T163 (kane-gate-pack block 8).

Records, from OUTSIDE the process under test, which new top-level windows and which
browser / auth-broker processes appeared during a watched interval, and classifies them.

Why it has to be external. The device-code lane has an in-process seam
(EntraToken.DeviceCodePromptForTests), so AgentNonInteractiveTests can already prove
that lane never asks a human for anything. The BROWSER lane has no equivalent:
InteractiveBrowserCredential.AuthenticateAsync (EntraToken.cs) hands the URL to the
machine's default browser, so "no window appeared" is not observable from inside the
process at all. This watches the desktop instead of inferring from a refusal string.

WHAT THIS CANNOT SEE, and why the caller must act on it. If a browser of the same family
was ALREADY RUNNING when the watch started, a sign-in opened as a BACKGROUND TAB creates
no new top-level window and no new process family, so nothing here will observe it. That
is not a tuning problem, it is a limit of watching windows. `browserLaneObservable` is
therefore emitted as $false whenever a watched browser family is present at baseline, and
run-block8.ps1 turns that into VOID rather than PASS. `NOTHING_APPEARED` from this script
NEVER means "no sign-in happened" on its own; it means "nothing was observed", and only a
run with browserLaneObservable=$true licenses the stronger reading.

Other things it cannot observe, all carried into the gate's `unproven` list: a hidden or
minimised window; a window on a different or secure desktop, which EnumWindows cannot
enumerate at all; a console prompt (no window is involved); and anything that lived and
died inside one poll gap.

Usage:
  # start watching (runs until <OutDir>\stop exists)
  pwsh tools/release/signin-sentinel.ps1 -OutDir C:\tmp\watch1

  # in another shell: do the thing, then
  New-Item -ItemType File C:\tmp\watch1\stop

Writes:
  <OutDir>\baseline.json     windows + processes at start
  <OutDir>\detections.jsonl  one line per detection
  <OutDir>\summary.json      verdict + counts + poll statistics + claim strength

Window titles are REDACTED by default: a real sign-in title carries the tenant or
account name ("Sign in to your account and 4 more pages - <tenant> - Microsoft Edge"),
and committed release evidence must never name a real tenant. The classification
(SIGNIN_TITLE_MATCH) survives redaction, which is the part the gate asserts on.
Pass -NoRedact for local interactive debugging only.
#>
param(
    [Parameter(Mandatory)][string]$OutDir,
    # 0 = no throttle: poll as fast as the machine allows, which gives the tightest detection
    # window. The number that matters is the MEASURED gap (pollGapMsMean / pollGapMsMax in the
    # summary), not a requested interval, so the measurement is what the gate reports and
    # reasons about. A positive value throttles deliberately, for a long watch where CPU cost
    # matters more than resolution; it makes the blind gap WIDER, so raising it weakens the run.
    [int]$IntervalMs = 0,
    [int]$MaxSeconds = 600,
    [switch]$NoRedact,
    # SELF-TEST ONLY. Makes the window enumerator return nothing, simulating an observer that
    # has silently stopped observing. Everything downstream then looks perfectly clean, which
    # is exactly the failure mode a gate cannot be allowed to have. run-block8.ps1
    # -SimulateBlindObserver uses this to prove a blind observer is caught rather than trusted.
    [switch]$SelfTestBlind
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -Language CSharp -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class SemSentinelWin {
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);

    // Every visible top-level window EnumWindows will admit. Nothing is filtered here;
    // `owner` and `toolWindow` are recorded as flags and the CALLER decides what they mean,
    // so the filtering rule lives in one readable place instead of inside the P/Invoke.
    // An OWNED window (a dialog of a parent) is exactly what a sign-in pop-up is, so it
    // must never be filtered out.
    // Note EnumWindows only ever sees the CALLER's desktop: a surface raised on another
    // desktop, or on the secure desktop, is invisible to this and always will be.
    public static List<string> Snapshot() {
        var list = new List<string>();
        EnumWindows((h, l) => {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(1024);
            GetWindowTextW(h, sb, 1024);
            string title = sb.ToString();
            var cb = new StringBuilder(512);
            GetClassNameW(h, cb, 512);
            string cls = cb.ToString();
            uint pid; GetWindowThreadProcessId(h, out pid);
            IntPtr owner = GetWindow(h, 4 /*GW_OWNER*/);
            int exStyle = GetWindowLong(h, -20 /*GWL_EXSTYLE*/);
            bool toolWindow = (exStyle & 0x00000080) != 0; // WS_EX_TOOLWINDOW
            // hwnd is not stable-comparable across a create/destroy, so identity is
            // (pid, class, title). Untitled windows are kept and flagged rather than
            // dropped: a sign-in surface USUALLY carries a title, but a window caught
            // mid-creation may not have one yet, so absence of a title is recorded, not
            // treated as proof the window is uninteresting.
            list.Add(string.Join("\u001f", new string[] {
                pid.ToString(), cls, title,
                owner == IntPtr.Zero ? "0" : "1",
                toolWindow ? "1" : "0",
                h.ToString()
            }));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
'@

# Processes that COMMONLY host a sign-in surface. This list is a heuristic, not a proof: it
# cannot be exhaustive, because any Chromium build can be someone's default browser. A browser
# missing from it weakens two things at once: it would not count as evidence, AND it would not
# be spotted at baseline, so the run would claim to be quieter than it was. Add to it rather
# than trusting it.
#
# Split in two, because the halves mean different things at BASELINE.
#
# A BROWSER already running is what creates the blind spot: it can hold a sign-in as a
# background tab, with no new window and no new process family. Its presence at baseline is
# what sets browserLaneObservable = $false.
#
# A BROKER already running does NOT create that blind spot: brokers raise their own window or
# process when they are used, so a running broker is normal on Windows and harmless here. If
# these were treated the same, Microsoft.AAD.BrokerPlugin sitting idle (as it usually does)
# would void the gate permanently and nobody could ever certify anything.
#
# Both halves are watched for NEW appearances during the run, and both count as evidence.
$browserProcs = @(
    'msedge', 'chrome', 'firefox', 'brave', 'opera', 'opera_gx', 'iexplore', 'msedgewebview2',
    'vivaldi', 'arc', 'chromium', 'thorium', 'ungoogled-chromium', 'waterfox', 'librewolf'
)
$brokerProcs = @(
    'WWAHost', 'AuthHost', 'Microsoft.AAD.BrokerPlugin', 'BrowserCore',
    'CredentialUIBroker', 'LogonUI'
)
$watchProcs = $browserProcs + $brokerProcs
# Titles that are STRONG evidence of a sign-in surface. Also a heuristic: a localised or
# renamed surface can miss this pattern entirely, which is why process attribution exists
# as a second, independent route.
$signinTitle = '(?i)(sign in|sign-in|signin|log in|login|microsoft account|verify your identity|enter code|authenticat|permissions requested|pick an account|stay signed in|two-factor|mfa|entra|oauth|consent)'

# Redaction is applied AFTER classification, so SIGNIN_TITLE_MATCH survives it; what is
# dropped is the tenant and account name a real sign-in title carries. Kept evidence must
# never name a real tenant.
function Protect-Title([string]$t) {
    if ($NoRedact) { return $t }
    if ([string]::IsNullOrEmpty($t)) { return $t }
    return "<redacted len=$($t.Length)>"
}

function Get-WinSet {
    $h = @{}
    # -SelfTestBlind stops the enumeration here, so every later stage behaves exactly as it
    # would if EnumWindows silently returned nothing on a real machine.
    if ($SelfTestBlind) { return $h }
    foreach ($row in [SemSentinelWin]::Snapshot()) {
        $p = $row -split "`u{001f}"
        # identity excludes hwnd, so a window that merely moved is not "new"
        $key = "$($p[0])|$($p[1])|$($p[2])"
        $h[$key] = [pscustomobject]@{
            pid = [int]$p[0]; class = $p[1]; title = $p[2]
            owned = $p[3] -eq '1'; tool = $p[4] -eq '1'; hwnd = $p[5]
        }
    }
    $h
}
function Get-ProcSet {
    $h = @{}
    foreach ($p in Get-Process -ErrorAction SilentlyContinue) {
        $h["$($p.Id)|$($p.ProcessName)"] = $p.ProcessName
    }
    $h
}

$baseWin = Get-WinSet
$baseProc = Get-ProcSet
$baseProcNames = @{}
foreach ($k in $baseProc.Keys) { $baseProcNames[$baseProc[$k]] = $true }

# Only a BROWSER at baseline creates the background-tab blind spot (see the list above).
$browsersAtBaseline = @($browserProcs | Where-Object { $baseProcNames.ContainsKey($_) })
$brokersAtBaseline  = @($brokerProcs  | Where-Object { $baseProcNames.ContainsKey($_) })

[pscustomobject]@{
    startedUtc   = (Get-Date).ToUniversalTime().ToString('o')
    intervalMs   = $IntervalMs
    windowCount  = $baseWin.Count
    processCount = $baseProc.Count
    redacted     = -not $NoRedact
    windows      = @($baseWin.Values | Sort-Object title | ForEach-Object {
        [pscustomobject]@{ pid = $_.pid; class = $_.class; title = (Protect-Title $_.title); owned = $_.owned; tool = $_.tool }
    })
    browsersPresentAtBaseline = $browsersAtBaseline
    brokersPresentAtBaseline  = $brokersAtBaseline
    browserLaneObservable     = ($browsersAtBaseline.Count -eq 0)
} | ConvertTo-Json -Depth 6 | Set-Content "$OutDir\baseline.json"

$detFile = "$OutDir\detections.jsonl"
Set-Content -Path $detFile -Value '' -NoNewline

$seen = @{}
$polls = 0
$gaps = [System.Collections.Generic.List[double]]::new()
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$last = $sw.Elapsed.TotalMilliseconds
$stop = "$OutDir\stop"
Write-Host "sentinel: watching. baseline $($baseWin.Count) windows / $($baseProc.Count) processes. touch '$stop' to finish."

while (-not (Test-Path $stop) -and $sw.Elapsed.TotalSeconds -lt $MaxSeconds) {
    $polls++
    $now = $sw.Elapsed.TotalMilliseconds
    $gaps.Add($now - $last); $last = $now

    $w = Get-WinSet
    foreach ($k in $w.Keys) {
        if ($baseWin.ContainsKey($k) -or $seen.ContainsKey("w:$k")) { continue }
        $seen["w:$k"] = $true
        $o = $w[$k]
        $pn = try { (Get-Process -Id $o.pid -ErrorAction Stop).ProcessName } catch { '<gone>' }
        # Classify BEFORE redacting, so the tenant name never has to be kept to keep the verdict.
        #
        # The claim is "no SIGN-IN surface appeared", not "no window of any kind appeared".
        # A tray app repainting (measured on a working machine: Lightshot, logioptionsplus_agent)
        # is not a sign-in surface, and counting it made the gate fail for reasons that have
        # nothing to do with T163. So a new window is hard evidence only when it is
        # ATTRIBUTABLE to a sign-in surface, by either of two independent routes:
        #   - its title names a sign-in (catches a surface hosted by anything, even an
        #     unexpected process), or
        #   - its owning process is a browser or auth broker (catches a sign-in surface whose
        #     title we failed to anticipate).
        # What this gives up is recorded as `unproven` by the runner: a sign-in surface both
        # hosted outside the watched families AND titled in a way the pattern misses would be
        # missed. The engine has no WAM or WebView2 path, so the browser lane is the real risk
        # and both routes cover it.
        # A tool window (WS_EX_TOOLWINDOW: no taskbar button, thin caption) is a palette or
        # tray affordance, never a sign-in surface, so it is demoted rather than silently
        # dropped: it stays in the record as IGNORED_TOOL_WINDOW so a reader can audit the
        # decision instead of taking the filter on trust.
        $inWatchedProc = $watchProcs -contains $pn
        $kind = if ($o.tool -and -not ($o.title -match $signinTitle)) { 'IGNORED_TOOL_WINDOW' }
                elseif ($o.title -match $signinTitle) { 'SIGNIN_TITLE_MATCH' }
                elseif ($inWatchedProc) { 'NEW_WINDOW_IN_WATCHED_PROCESS' }
                elseif ($o.title.Length -eq 0) { 'UNRELATED_WINDOW_UNTITLED' }
                else { 'UNRELATED_WINDOW' }
        [pscustomobject]@{
            atMs = [int]$now; kind = $kind; title = (Protect-Title $o.title); class = $o.class
            pid = $o.pid; process = $pn; owned = $o.owned; tool = $o.tool
        } | ConvertTo-Json -Compress | Add-Content $detFile
    }

    $p = Get-ProcSet
    foreach ($k in $p.Keys) {
        if ($baseProc.ContainsKey($k) -or $seen.ContainsKey("p:$k")) { continue }
        $seen["p:$k"] = $true
        $name = $p[$k]
        if ($watchProcs -notcontains $name) { continue }
        # A browser that was ALREADY running spawns and reaps child processes constantly
        # (measured: Edge did so 3.8s into an idle watch). Counting those as evidence makes
        # the gate fail at random, so only a browser family ABSENT at baseline is hard
        # evidence.
        #
        # This is exactly why browserLaneObservable exists. Ignoring churn is necessary, but
        # it is ALSO the hole: when the family was already up, a background sign-in tab
        # produces nothing but churn and an unchanged top-level title, so this script cannot
        # see it. Do NOT read "we fall back to the window title" as coverage — a background
        # tab changes no top-level title either. The only honest response is the caller
        # refusing to certify, which run-block8.ps1 does by reporting VOID.
        $newFamily = -not $baseProcNames.ContainsKey($name)
        [pscustomobject]@{
            atMs = [int]$now
            kind = if ($newFamily) { 'NEW_WATCHED_PROCESS' } else { 'WATCHED_PROCESS_CHURN' }
            process = $name
            pid = [int]($k -split '\|')[0]
            newFamily = $newFamily
        } | ConvertTo-Json -Compress | Add-Content $detFile
    }

    # ARMED handshake. The caller must not start the action until a full poll has COMPLETED,
    # or a surface raised between the baseline write and the first poll is missed and nothing
    # records that it could have been. baseline.json existing proves only that the baseline
    # was taken. This marker is the real "watching now" signal, and it carries the gap so the
    # blind window is measured instead of assumed away.
    if ($polls -eq 1) {
        [pscustomobject]@{
            armedUtc            = (Get-Date).ToUniversalTime().ToString('o')
            firstPollEndedAtMs  = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
            baselineToArmedMs   = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
        } | ConvertTo-Json | Set-Content "$OutDir\armed.json"
    }

    # Honour the requested cadence when one was asked for. At the default of 0 this is skipped
    # entirely and the loop runs flat out, so `intervalMsRequested: 0` in the evidence means
    # exactly what it says rather than naming a delay nobody applied. Subtract the work already
    # done in this iteration so the gap approximates the interval rather than exceeding it.
    if ($IntervalMs -gt 0) {
        $spent = $sw.Elapsed.TotalMilliseconds - $now
        $rest = $IntervalMs - $spent
        if ($rest -gt 0) { Start-Sleep -Milliseconds ([int][math]::Ceiling($rest)) }
    }
}

$dets = @()
if ((Get-Item $detFile).Length -gt 0) {
    $dets = @(Get-Content $detFile | Where-Object { $_ } | ForEach-Object { $_ | ConvertFrom-Json })
}
# Hard evidence = attributable to a sign-in surface. UNRELATED_WINDOW* is recorded in full
# (so a reader can see exactly what was ignored and challenge it) but never moves the verdict.
$hard = @($dets | Where-Object { $_.kind -in @('SIGNIN_TITLE_MATCH', 'NEW_WINDOW_IN_WATCHED_PROCESS', 'NEW_WATCHED_PROCESS') })

$armed = if (Test-Path "$OutDir\armed.json") { Get-Content "$OutDir\armed.json" -Raw | ConvertFrom-Json } else { $null }
[pscustomobject]@{
    stoppedUtc       = (Get-Date).ToUniversalTime().ToString('o')
    watchedSeconds   = [math]::Round($sw.Elapsed.TotalSeconds, 2)
    polls            = $polls
    intervalMsRequested = $IntervalMs
    pollGapMsMean    = if ($gaps.Count) { [math]::Round(($gaps | Measure-Object -Average).Average, 1) } else { 0 }
    pollGapMsMax     = if ($gaps.Count) { [math]::Round(($gaps | Measure-Object -Maximum).Maximum, 1) } else { 0 }
    # The blind gap between taking the baseline and completing the first poll. Reported, not
    # assumed away: anything that appeared and vanished inside it was never observable.
    baselineToArmedMs = if ($armed) { $armed.baselineToArmedMs } else { $null }
    armed            = [bool]$armed
    # LIVENESS. A real desktop always has visible top-level windows, so a zero here means the
    # enumerator returned nothing and this watch observed nothing it could have observed. The
    # runner treats it as a hard failure rather than as a clean run.
    baselineWindowCount = $baseWin.Count
    selfTestBlind    = [bool]$SelfTestBlind
    detections       = $dets.Count
    hardDetections   = $hard.Count
    churnIgnored     = @($dets | Where-Object { $_.kind -eq 'WATCHED_PROCESS_CHURN' }).Count
    unrelatedIgnored = @($dets | Where-Object { $_.kind -like 'UNRELATED_WINDOW*' }).Count
    toolWindowIgnored = @($dets | Where-Object { $_.kind -eq 'IGNORED_TOOL_WINDOW' }).Count
    signinDetections = @($dets | Where-Object { $_.kind -eq 'SIGNIN_TITLE_MATCH' }).Count
    # "NOTHING_APPEARED" means NOTHING WAS OBSERVED. It is not a claim that nothing happened.
    # Read it together with browserLaneObservable before drawing any conclusion.
    verdict          = if ($hard.Count -eq 0) { 'NOTHING_APPEARED' } else { 'SOMETHING_APPEARED' }
    browsersRunningAtBaseline = $browsersAtBaseline
    # THE load-bearing field. False means a browser family was already running, so a sign-in
    # opened as a background tab would have produced no new top-level window and no new
    # process family, and this watch could not have seen it. A NOTHING_APPEARED verdict on
    # such a run therefore licenses NO conclusion about the browser lane, and run-block8.ps1
    # reports VOID rather than PASS.
    browserLaneObservable = ($browsersAtBaseline.Count -eq 0)
    claimStrength    = if ($browsersAtBaseline.Count -eq 0) { 'STRONG_quiet_machine' } else { 'REDUCED_browser_already_running' }
    redacted         = -not $NoRedact
    items            = $dets
} | ConvertTo-Json -Depth 6 | Set-Content "$OutDir\summary.json"

Write-Host "sentinel: done. polls=$polls verdict=$(if($hard.Count -eq 0){'NOTHING_APPEARED'}else{'SOMETHING_APPEARED'}) detections=$($dets.Count)"
