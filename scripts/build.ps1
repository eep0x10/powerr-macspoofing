param([string]$Output = 'dist/win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = (Get-Command dotnet.exe -ErrorAction SilentlyContinue).Source
if (-not $sdk -or -not (& $sdk --list-sdks)) {
    $fallback = Join-Path $env:USERPROFILE 'scoop\apps\dotnet-sdk\current\dotnet.exe'
    if (-not (Test-Path $fallback)) { throw 'SDK .NET 10 não encontrado.' }
    $sdk = $fallback
}
$target = Join-Path $root $Output
& $sdk publish (Join-Path $root 'desktop\PowerR.Desktop.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' -o $target
if ($LASTEXITCODE -ne 0) { throw 'Falha ao publicar PowerR.exe.' }
$exe = Join-Path $target 'PowerR.exe'
if (-not (Test-Path $exe)) { throw 'Executável ausente após publicação.' }
Get-FileHash -Algorithm SHA256 $exe | Format-List
