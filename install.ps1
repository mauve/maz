$ErrorActionPreference = 'Stop'

$Repo       = 'mauve/maz'
$InstallDir = if ($env:MAZ_INSTALL_DIR) { $env:MAZ_INSTALL_DIR } else { "$HOME\.local\bin" }

# Detect architecture
$Arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { 'x64'   }
    'Arm64' { 'arm64' }
    default { Write-Error "Unsupported architecture: $_"; exit 1 }
}

$Asset = "maz-win-$Arch.exe"

# Resolve latest release tag
Write-Host 'Fetching latest release...'
$Release = Invoke-RestMethod "https://api.github.com/repos/$Repo/releases/latest"
$Tag     = $Release.tag_name

$Url  = "https://github.com/$Repo/releases/download/$Tag/$Asset"
$Dest = Join-Path $InstallDir 'maz.exe'

Write-Host "Downloading maz $Tag (win/$Arch)..."
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Invoke-WebRequest $Url -OutFile $Dest

Write-Host "Installed: $Dest"

# Warn (or offer to fix) if not in PATH
$UserPath = [Environment]::GetEnvironmentVariable('PATH', 'User')
if ($UserPath -notlike "*$InstallDir*") {
    Write-Warning "$InstallDir is not in your PATH."
    Write-Host ''
    Write-Host 'To add it permanently, run:'
    Write-Host ''
    Write-Host "  [Environment]::SetEnvironmentVariable('PATH', `"$InstallDir;`$env:PATH`", 'User')"
    Write-Host ''
    Write-Host 'To add it for the current session only:'
    Write-Host "  `$env:PATH = `"$InstallDir;`$env:PATH`""
    Write-Host ''

    $answer = Read-Host 'Add to your user PATH now? [y/N]'
    if ($answer -match '^[Yy]') {
        [Environment]::SetEnvironmentVariable('PATH', "$InstallDir;$UserPath", 'User')
        $env:PATH = "$InstallDir;$env:PATH"
        Write-Host 'PATH updated.'
    }
}
