param([ValidateSet('Build', 'Publish', 'Run')][string]$Action = 'Build')
$ErrorActionPreference = 'Stop'

try {
    $projectRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += Join-Path $env:DOTNET_ROOT 'dotnet.exe' }
    $onPath = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates += $onPath.Source }
    $candidates += Join-Path $env:USERPROFILE '.cache\tesla-v100-controller\dotnet\dotnet.exe'
    $dotnetExecutable = $null
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (!(Test-Path -LiteralPath $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>&1 | Out-String
        if ($LASTEXITCODE -eq 0 -and $sdks -match '(?m)^10\.') {
            $dotnetExecutable = $candidate
            break
        }
    }
    if (!$dotnetExecutable) {
        throw 'Install the .NET 10 SDK, or set DOTNET_ROOT to its installation directory.'
    }

    # Keep both the build and the debug apphost on the SDK/runtime selected above.
    $env:DOTNET_ROOT = Split-Path -Parent $dotnetExecutable
    $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $envPath = Join-Path $projectRoot '.vscode\dotnet.env'
    $environment = "DOTNET_ROOT=$env:DOTNET_ROOT`nDOTNET_ROOT_X64=$env:DOTNET_ROOT`n"
    [IO.File]::WriteAllText($envPath, $environment, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Using SDK: $dotnetExecutable"

    function Invoke-DotNet([string[]]$Arguments) {
        & $dotnetExecutable @Arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
    }

    Push-Location -LiteralPath $projectRoot
    try {
        switch ($Action) {
            'Build' { Invoke-DotNet @('build', 'TeslaV100Controller.sln', '-c', 'Debug') }
            { $_ -in @('Publish', 'Run') } {
                Invoke-DotNet @('publish', 'src/TeslaV100Controller', '-c', 'Release', '-r', 'win-x64',
                    '--self-contained', 'true', '-p:PublishSingleFile=true',
                    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true',
                    '-o', 'bin/Release')
                if ($Action -eq 'Run') {
                    $exe = Join-Path $projectRoot 'bin\Release\TeslaV100Controller.exe'
                    Write-Host 'Starting the application as administrator. Accept the Windows UAC request.'
                    $process = Start-Process -FilePath $exe -Verb RunAs -WindowStyle Normal -WorkingDirectory (Split-Path -Parent $exe) -Wait -PassThru
                    if ($process.ExitCode -ne 0) { throw "Application exited with code $($process.ExitCode)" }
                }
            }
        }
    } finally { Pop-Location }
    exit 0
} catch {
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}
