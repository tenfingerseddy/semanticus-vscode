#Requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Preflight', 'F5Preflight', 'Install', 'Upgrade', 'Uninstall', 'Performance')]
    [string] $Action,

    [string] $Vsix,
    [string] $PreviousVsix,
    [string] $LargeModelFixture,
    [string] $EvidenceDirectory = (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Semanticus-RC-Evidence'),
    [ValidateSet('win32-x64', 'win32-arm64', 'linux-x64', 'darwin-x64', 'darwin-arm64')]
    [string] $Target = 'win32-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$extensionRoot = Join-Path $repoRoot 'Semanticus.VSCode'
$extensionId = 'semanticus-vscode.semanticus-vscode'
$startedAt = [DateTimeOffset]::UtcNow

New-Item -ItemType Directory -Force -Path $EvidenceDirectory | Out-Null
$evidenceRoot = (Resolve-Path $EvidenceDirectory).Path

function Require-Command([string] $Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command was not found: $Name"
    }
}

function Resolve-RequiredFile([string] $Path, [string] $Label) {
    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "$Label is required for action $Action."
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Label was not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path).Path
}

function Invoke-Logged([string] $Name, [scriptblock] $Command) {
    $logPath = Join-Path $evidenceRoot "$Name.log"
    & $Command *>&1 | Tee-Object -FilePath $logPath
    $exitCode = $LASTEXITCODE
    if ($null -ne $exitCode -and $exitCode -ne 0) {
        throw "$Name failed with exit code $exitCode. See $logPath"
    }
}

function Get-InstalledExtension {
    $matches = @(& code --list-extensions --show-versions | Where-Object {
        $_ -match "^$([regex]::Escape($extensionId))@"
    })
    if ($LASTEXITCODE -ne 0) { throw 'VS Code could not list installed extensions.' }
    return $matches
}

function Assert-Installed {
    $installed = @(Get-InstalledExtension)
    if ($installed.Count -ne 1) {
        throw "Expected exactly one installed $extensionId entry, found $($installed.Count)."
    }
    return $installed[0]
}

function Assert-NotInstalled {
    $installed = @(Get-InstalledExtension)
    if ($installed.Count -ne 0) {
        throw "$extensionId is still installed: $($installed -join ', ')"
    }
}

function Write-JsonEvidence([string] $Name, [hashtable] $Data) {
    $path = Join-Path $evidenceRoot "$Name.json"
    $Data | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    Write-Host "Evidence: $path"
}

switch ($Action) {
    'Preflight' {
        Require-Command git
        Require-Command gh
        Require-Command node
        Require-Command code

        $vsixPath = Resolve-RequiredFile $Vsix 'VSIX path'
        $status = @(& git -C $repoRoot status --porcelain=v1)
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the release worktree.' }
        if ($status.Count -ne 0) {
            throw "The release worktree is not clean:`n$($status -join "`n")"
        }

        $sha = (& git -C $repoRoot rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the release SHA.' }
        $mainSha = (& git -C $repoRoot rev-parse origin/main).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Could not resolve origin/main. Fetch it before running preflight.' }
        if ($sha -ne $mainSha) {
            throw "Release checkout $sha does not equal origin/main $mainSha."
        }

        $runsJson = & gh run list --commit $sha --workflow CI --limit 20 --json databaseId,headSha,status,conclusion,url,event
        if ($LASTEXITCODE -ne 0) { throw 'Could not read GitHub CI runs.' }
        $runs = @($runsJson | ConvertFrom-Json)
        $green = @($runs | Where-Object { $_.headSha -eq $sha -and $_.status -eq 'completed' -and $_.conclusion -eq 'success' })
        if ($green.Count -eq 0) {
            throw "No successful completed CI run exists for exact RC SHA $sha."
        }

        $verifier = Join-Path $extensionRoot 'scripts\verify-vsix.mjs'
        if (-not (Test-Path -LiteralPath $verifier -PathType Leaf)) {
            throw 'The allow-list VSIX verifier is absent. T118 must merge before RC acceptance starts.'
        }
        Invoke-Logged 'vsix-verification' { node $verifier $vsixPath $Target }

        $package = Get-Content -LiteralPath (Join-Path $extensionRoot 'package.json') -Raw | ConvertFrom-Json
        $hash = Get-FileHash -LiteralPath $vsixPath -Algorithm SHA256
        $codeVersion = @(& code --version)
        if ($LASTEXITCODE -ne 0) { throw 'Could not read the VS Code version.' }

        Write-JsonEvidence 'preflight' @{
            action = $Action
            capturedUtc = $startedAt.ToString('O')
            rcSha = $sha
            originMainSha = $mainSha
            ciRun = $green[0]
            target = $Target
            vsixPath = $vsixPath
            vsixSha256 = $hash.Hash
            vsixBytes = (Get-Item -LiteralPath $vsixPath).Length
            extensionId = $extensionId
            publisher = $package.publisher
            version = $package.version
            license = $package.license
            vscode = $codeVersion
            operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
            architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        }
    }

    'F5Preflight' {
        Require-Command dotnet
        Require-Command npm
        Invoke-Logged 'f5-engine-build' { dotnet build (Join-Path $repoRoot 'Semanticus.Engine') -c Debug }
        Push-Location $extensionRoot
        try {
            Invoke-Logged 'f5-npm-ci' { npm ci --no-fund --no-audit }
            Invoke-Logged 'f5-extension-tests' { npm test }
            Invoke-Logged 'f5-webview-build' { npm run build:webview }
            Invoke-Logged 'f5-extension-compile' { npm run compile }
        } finally {
            Pop-Location
        }
        $dll = Join-Path $repoRoot 'Semanticus.Engine\bin\Debug\net8.0\Semanticus.Engine.dll'
        if (-not (Test-Path -LiteralPath $dll -PathType Leaf)) { throw "Debug engine was not produced: $dll" }
        Write-Host "Set semanticus.engineDll in the Extension Development Host to: $dll"
        Write-Host 'Then press F5 and execute the interaction checklist in docs/rc-acceptance.md.'
    }

    'Install' {
        Require-Command code
        $vsixPath = Resolve-RequiredFile $Vsix 'VSIX path'
        if (@(Get-InstalledExtension).Count -ne 0) {
            throw "$extensionId is already installed. Use Upgrade or Uninstall first."
        }
        Invoke-Logged 'clean-install' { code --install-extension $vsixPath --force }
        $installed = Assert-Installed
        Write-JsonEvidence 'clean-install' @{
            action = $Action
            capturedUtc = $startedAt.ToString('O')
            installed = $installed
            vsixSha256 = (Get-FileHash -LiteralPath $vsixPath -Algorithm SHA256).Hash
        }
    }

    'Upgrade' {
        Require-Command code
        $previousPath = Resolve-RequiredFile $PreviousVsix 'Previous VSIX path'
        $vsixPath = Resolve-RequiredFile $Vsix 'RC VSIX path'
        if ((Get-FileHash $previousPath -Algorithm SHA256).Hash -eq (Get-FileHash $vsixPath -Algorithm SHA256).Hash) {
            throw 'Previous and RC VSIX files are identical.'
        }
        if (@(Get-InstalledExtension).Count -ne 0) {
            Invoke-Logged 'upgrade-remove-existing' { code --uninstall-extension $extensionId }
            Assert-NotInstalled
        }
        Invoke-Logged 'upgrade-install-previous' { code --install-extension $previousPath --force }
        $before = Assert-Installed
        Invoke-Logged 'upgrade-install-rc' { code --install-extension $vsixPath --force }
        $after = Assert-Installed
        if ($before -eq $after) { throw "Upgrade did not change the installed version: $after" }
        Write-JsonEvidence 'upgrade' @{
            action = $Action
            capturedUtc = $startedAt.ToString('O')
            before = $before
            after = $after
            previousSha256 = (Get-FileHash -LiteralPath $previousPath -Algorithm SHA256).Hash
            rcSha256 = (Get-FileHash -LiteralPath $vsixPath -Algorithm SHA256).Hash
        }
    }

    'Uninstall' {
        Require-Command code
        Assert-Installed | Out-Null
        Invoke-Logged 'uninstall' { code --uninstall-extension $extensionId }
        Assert-NotInstalled
        Write-JsonEvidence 'uninstall' @{
            action = $Action
            capturedUtc = $startedAt.ToString('O')
            extensionId = $extensionId
            absentAfterCommand = $true
        }
    }

    'Performance' {
        Require-Command dotnet
        $largeFixture = Resolve-RequiredFile $LargeModelFixture 'Large-model fixture'
        $baseline = Join-Path $repoRoot 'docs\release-evidence\performance-baseline-windows-x64.json'
        $standardOutput = Join-Path $evidenceRoot 'performance-standard.json'
        $largeOutput = Join-Path $evidenceRoot 'performance-large-model.json'
        Invoke-Logged 'performance-build' { dotnet build (Join-Path $repoRoot 'Semanticus.PerfSmoke') -c Release }
        Invoke-Logged 'performance-standard' {
            dotnet run --project (Join-Path $repoRoot 'Semanticus.PerfSmoke') -c Release --no-build -- --baseline $baseline --output $standardOutput
        }
        Invoke-Logged 'performance-large-model' {
            dotnet run --project (Join-Path $repoRoot 'Semanticus.PerfSmoke') -c Release --no-build -- --fixture $largeFixture --output $largeOutput
        }
    }
}

Write-Host "Completed $Action. Evidence directory: $evidenceRoot"
