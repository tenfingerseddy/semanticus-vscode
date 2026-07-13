param(
    [string]$Solution = 'Semanticus.sln'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Get-NuGetVulnerabilities($report) {
    $found = @()
    foreach ($project in @($report.projects)) {
        foreach ($framework in @($project.frameworks)) {
            foreach ($group in @('topLevelPackages', 'transitivePackages')) {
                foreach ($package in @($framework.$group)) {
                    if ($null -eq $package) { continue }
                    foreach ($vulnerability in @($package.vulnerabilities)) {
                        if ($null -eq $vulnerability) { continue }
                        $found += [pscustomobject]@{
                            Project = $project.path
                            Package = $package.id
                            Version = $package.resolvedVersion
                            Severity = $vulnerability.severity
                            Advisory = $vulnerability.advisoryUrl
                        }
                    }
                }
            }
        }
    }
    return @($found)
}

function ConvertFrom-NuGetJsonOutput($lines) {
    $json = @($lines) -join [Environment]::NewLine
    if ([string]::IsNullOrWhiteSpace($json)) { throw 'NuGet vulnerability scan returned empty output.' }
    try { return $json | ConvertFrom-Json }
    catch { throw 'NuGet vulnerability scan did not return valid JSON: ' + $_.Exception.Message }
}

# Guard the parser itself. `dotnet list package` returns exit code zero even when it reports vulnerabilities,
# so a parser that silently misses the JSON fields would turn this into a false green gate.
$parserFixture = @'
{"projects":[{"path":"fixture.csproj","frameworks":[{"transitivePackages":[{"id":"Unsafe.Package","resolvedVersion":"1.0.0","vulnerabilities":[{"severity":"high","advisoryUrl":"https://example.invalid/advisory"}]}]}]}]}
'@ | ConvertFrom-Json
$parserResult = @(Get-NuGetVulnerabilities $parserFixture)
if ($parserResult.Count -ne 1 -or $parserResult[0].Package -ne 'Unsafe.Package') {
    throw 'Dependency audit parser self-test failed.'
}
$singleLine = '{"projects":[{"path":"single.csproj","frameworks":[]}],"sources":["https://example.invalid"]}'
$singleLineResult = ConvertFrom-NuGetJsonOutput $singleLine
if (@($singleLineResult.projects).Count -ne 1 -or $singleLineResult.projects[0].path -ne 'single.csproj') {
    throw 'Dependency audit single-line JSON self-test failed.'
}

& dotnet restore $Solution --nologo -warnaserror:NU1900
if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE." }

$raw = & dotnet list $Solution package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw "NuGet vulnerability scan failed with exit code $LASTEXITCODE." }
$report = ConvertFrom-NuGetJsonOutput @($raw)
if (@($report.projects).Count -eq 0 -or @($report.sources).Count -eq 0) {
    throw 'NuGet vulnerability scan returned no projects or advisory sources; refusing a false green.'
}

$nugetFindings = @(Get-NuGetVulnerabilities $report)
if ($nugetFindings.Count -gt 0) {
    $lines = $nugetFindings | ForEach-Object {
        "[$($_.Severity)] $($_.Package) $($_.Version) in $($_.Project): $($_.Advisory)"
    }
    throw "NuGet vulnerability gate failed with $($nugetFindings.Count) finding(s):`n$($lines -join "`n")"
}

$npmRoots = @()
$pending = [System.Collections.Generic.Stack[string]]::new()
$pending.Push((Join-Path $root 'Semanticus.VSCode'))
while ($pending.Count -gt 0) {
    $dir = $pending.Pop()
    if (Test-Path -LiteralPath (Join-Path $dir 'package-lock.json')) { $npmRoots += $dir }
    foreach ($child in @(Get-ChildItem -LiteralPath $dir -Directory -Force)) {
        if ($child.Name -in @('node_modules', '.git')) { continue }
        $pending.Push($child.FullName)
    }
}
$npmRoots = @($npmRoots | Sort-Object -Unique)
if ($npmRoots.Count -eq 0) { throw 'npm vulnerability gate found no package-lock.json files.' }
foreach ($dir in $npmRoots) {
    $relative = [System.IO.Path]::GetRelativePath($root, $dir).Replace('\', '/')
    if (-not (Test-Path -LiteralPath (Join-Path $dir 'package-lock.json'))) {
        throw "npm vulnerability gate cannot find $relative/package-lock.json."
    }
    Push-Location $dir
    try {
        & npm audit --audit-level=low
        if ($LASTEXITCODE -ne 0) { throw "npm vulnerability gate failed in $relative with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}

Write-Output "Dependency audit PASS: $(@($report.projects).Count) .NET projects and $($npmRoots.Count) npm lockfiles; zero known vulnerabilities."
