param(
    [string] $MakeNsis = "makensis.exe"
)

$ErrorActionPreference = "Stop"
$repo = [IO.Path]::GetFullPath([IO.Path]::Combine($PSScriptRoot, ".."))
$dist = [IO.Path]::Combine($repo, "dist")
$project = [IO.Path]::Combine($repo, "src", "DeckRemote", "DeckRemote.csproj")
$win = [IO.Path]::Combine($dist, "win-x64")
$steam = [IO.Path]::Combine($dist, "steamos")
$steamApp = [IO.Path]::Combine($steam, "app")

dotnet publish $project --configuration Release --runtime win-x64 --self-contained true --output $win -m:1
if ($LASTEXITCODE -ne 0) { throw "Windows publish failed." }

dotnet publish $project --configuration Release --runtime linux-x64 --self-contained true --output $steamApp -m:1
if ($LASTEXITCODE -ne 0) { throw "SteamOS publish failed." }

[IO.File]::Copy([IO.Path]::Combine($PSScriptRoot, "install-steamos.sh"), [IO.Path]::Combine($steam, "install.sh"), $true)
[IO.File]::Copy([IO.Path]::Combine($repo, "src", "DeckRemote", "Assets", "deck-remote.png"),
    [IO.Path]::Combine($steam, "deck-remote.png"), $true)
[IO.File]::Copy([IO.Path]::Combine($repo, "README.md"), [IO.Path]::Combine($steam, "README.md"), $true)

tar.exe -czf ([IO.Path]::Combine($dist, "DeckRemote-0.4.1-steamos-x64.tar.gz")) -C $steam .
if ($LASTEXITCODE -ne 0) { throw "SteamOS packaging failed." }

& $MakeNsis /V2 /INPUTCHARSET UTF8 ([IO.Path]::Combine($PSScriptRoot, "DeckRemote.nsi"))
if ($LASTEXITCODE -ne 0) { throw "Windows installer compilation failed." }
