# Writes repo.json (the custom plugin repository Dalamud reads) from the manifest the build produced.
# Usage: pwsh tools/make-repo-json.ps1 [-Changelog "text"]
param(
    [string]$Manifest = "$PSScriptRoot/../PriceScout/bin/Release/PriceScout.json",
    [string]$Output = "$PSScriptRoot/../repo.json",
    [string]$Changelog = ""
)

$ErrorActionPreference = 'Stop'
$m = Get-Content $Manifest -Raw | ConvertFrom-Json
$download = 'https://github.com/LichByte/PriceScout/releases/latest/download/latest.zip'

$entry = [ordered]@{
    Author              = $m.Author
    Name                = $m.Name
    InternalName        = $m.InternalName
    AssemblyVersion     = $m.AssemblyVersion
    Description         = $m.Description
    Punchline           = $m.Punchline
    ApplicableVersion   = $m.ApplicableVersion
    RepoUrl             = $m.RepoUrl
    IconUrl             = $m.IconUrl
    Tags                = $m.Tags
    DalamudApiLevel     = $m.DalamudApiLevel
    AcceptsFeedback     = $false
    DownloadLinkInstall = $download
    DownloadLinkUpdate  = $download
    DownloadLinkTesting = $download
    LastUpdate          = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    Changelog           = $Changelog
}

# Dalamud expects a JSON array, even for one plugin.
ConvertTo-Json -InputObject @($entry) -Depth 5 | Set-Content $Output -Encoding utf8
Write-Host "Wrote $Output for $($m.Name) $($m.AssemblyVersion)"
