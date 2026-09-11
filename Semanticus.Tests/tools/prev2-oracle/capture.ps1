# Regenerates the v1-compatibility goldens from the PRE-v2 parser.
#
#   pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1            # rewrite both goldens
#   pwsh -File Semanticus.Tests/tools/prev2-oracle/capture.ps1 -Check     # verify, write nothing
#
# Never point this at the working tree's parser. A golden produced by the code under test agrees with every
# change, including a wrong one, and the guard becomes a mirror.
[CmdletBinding()]
param([switch]$Check)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------
# THE PIN. This is the commit whose WorkflowParser.cs is the parser as it stood BEFORE format v2, and it
# is an immutable sha on purpose. It must NEVER be advanced.
#
# It used to read `origin/main`, which was a hole the size of the whole guard: the moment this branch
# merges, `origin/main` IS the v2 parser, so a re-capture would compile the code under test, call its
# output "pre-v2", and produce goldens that agree with whatever the parser now does. The guard would keep
# passing and would have stopped proving anything. A moving reference cannot be checked into a check.
#
# Advancing this line is only correct if the v1 RULESET changed, and the v1 ruleset is frozen forever
# (spec section 1.2), so there is no such correct reason. If a golden needs to change, the honest question
# is what changed in today's parser, never what this sha points at.
# ---------------------------------------------------------------------------------------------------
$PreV2Sha = '3214ee2c58dffaf690e6fb816713f3b9db6bfe48'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = (Resolve-Path (Join-Path $here '../../..')).Path
$prev2 = Join-Path $here 'obj/prev2'

Write-Host "repo: $repo"
git -C $repo cat-file -e "$PreV2Sha^{commit}" 2>$null
if ($LASTEXITCODE -ne 0) {
    git -C $repo fetch origin --quiet
    git -C $repo cat-file -e "$PreV2Sha^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) {
        throw "the pinned pre-v2 commit $PreV2Sha is not in this repository, even after fetching origin. Do NOT edit the pin to something reachable: find that commit. Without it there is no oracle, only today's parser agreeing with itself."
    }
}
Write-Host "pinned pre-v2 commit: $PreV2Sha (reachable)"

New-Item -ItemType Directory -Force -Path $prev2 | Out-Null
foreach ($f in @('WorkflowParser.cs', 'Workflow.cs')) {
    git -C $repo show "${PreV2Sha}:Semanticus.Engine/$f" | Set-Content -Path (Join-Path $prev2 $f) -Encoding utf8NoBOM
    if ($LASTEXITCODE -ne 0) { throw "could not read $f at $PreV2Sha" }
}

# The pin is also checked BEHAVIOURALLY, inside the harness itself (Program.cs,
# RefuseIfThisIsNotThePreV2Parser), which runs before a single fingerprint is written. It feeds the compiled
# parser two files that v1 accepts and v2 refuses and requires both to be accepted.
#
# That replaced a grep for the word 'schemaVersion' in the fetched source, which asked how the parser is
# SPELLED rather than what it DOES, and was wrong both ways: it rejected a pre-v2 commit whose comments
# merely mention the word, and it would have passed a post-v2 parser that reads the constant from another
# file. A behavioural probe cannot be fooled by either.

# ---------------------------------------------------------------------------------------------------
# NO DRIFT CHECK, deliberately, and this is not an omission.
#
# There used to be one here comparing this project's Fingerprint against the test's, line by line. It was
# weak in two ways Sol demonstrated: it saw only the first physical line of each L(...) call, so a change on
# a continuation line slipped past, and it could not see Hash() at all, which sits outside every L(...)
# line. It was deleted rather than repaired, because the property it claimed to enforce is ALREADY enforced,
# and far better, by the goldens themselves:
#
#   a golden is WRITTEN by this project's fingerprint and COMPARED by the test's fingerprint, so any
#   difference between the two surfaces as a failing golden comparison, naming the exact differing line.
#
# Drift in the test alone fails immediately. Drift here alone changes nothing until someone re-captures, and
# the moment they do, the test fails. Either way the mismatch is loud and specific. A source-text check on
# top of that added no coverage and did add the thing this slice keeps tripping over: a weak check that
# reads as a strong one.
#
# What that leaves uncovered, stated plainly: if someone changes BOTH copies the same way and re-captures,
# the goldens move and nothing objects. No mechanical check can catch that, because it is indistinguishable
# from deliberately extending the fingerprint, which is exactly what we did in round 6.
# ---------------------------------------------------------------------------------------------------

# Build honestly: no stale binary may survive, and a failed build must stop the run. The exit code used to
# be ignored and `dotnet run --no-build` followed, so a compile failure could leave the PREVIOUS binary to
# run and report success while the script printed a freshly fetched sha. An authoritative-looking wrong
# answer is worse than no answer.
$bin = Join-Path $here 'bin'
if (Test-Path $bin) { Remove-Item $bin -Recurse -Force }
dotnet build (Join-Path $here 'prev2-oracle.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "the pre-v2 oracle failed to build. Nothing was captured." }

$targets = @(
    @{ Golden = 'Semanticus.Tests/goldens/workflow-v1-parse.txt'
       Args = @("$repo/Semanticus.Engine/workflows", 'Semanticus.Engine/workflows',
                "$repo/Semanticus.Engine/workflow-templates", 'Semanticus.Engine/workflow-templates',
                "$repo/Semanticus.Engine/workflows-parked", 'Semanticus.Engine/workflows-parked') },
    @{ Golden = 'Semanticus.Tests/goldens/workflow-v1-shapes.txt'
       Args = @("$repo/Semanticus.Tests/fixtures/workflow-v1-shapes", 'Semanticus.Tests/fixtures/workflow-v1-shapes') }
)

# The harness writes its own file and the comparison is BYTE-WISE. Nothing here touches the content as
# text: a console code page in the middle of this path silently corrupted eleven lines the first time.
$failed = $false
foreach ($t in $targets) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("prev2-" + [Guid]::NewGuid().ToString('N').Substring(0, 8) + ".txt")
    dotnet run --project (Join-Path $here 'prev2-oracle.csproj') -c Release --no-build -- $tmp @($t.Args)
    if ($LASTEXITCODE -ne 0) { throw "capture failed for $($t.Golden). Nothing was written." }
    $path = Join-Path $repo $t.Golden
    if ($Check) {
        $a = [IO.File]::ReadAllBytes($tmp)
        $b = if (Test-Path $path) { [IO.File]::ReadAllBytes($path) } else { @() }
        if ([Linq.Enumerable]::SequenceEqual([byte[]]$a, [byte[]]$b)) { Write-Host "OK   $($t.Golden)" }
        else { Write-Host "DIFF $($t.Golden) (captured $($a.Length) bytes, on disk $($b.Length))"; $failed = $true }
    } else {
        Copy-Item $tmp $path -Force
        Write-Host "wrote $($t.Golden) ($((Get-Item $path).Length) bytes)"
    }
    Remove-Item $tmp -Force
}
if ($failed) { exit 1 }
