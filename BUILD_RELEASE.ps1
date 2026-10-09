param(
    [string]$Version = "0.8.7",
    [string]$InnoCompiler = "ISCC.exe"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root "artifacts\portable-$Version"
dotnet restore (Join-Path $root "LocaUltrator.sln") --configfile (Join-Path $root "NuGet.Config")
dotnet run --project (Join-Path $root "tests\LocaUltrator.Tests\LocaUltrator.Tests.csproj") -c Release --no-restore
dotnet publish (Join-Path $root "src\LocaUltrator.App\LocaUltrator.App.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -o $publish --no-restore
Copy-Item (Join-Path $root "README_HU.md") $publish -Force
Copy-Item (Join-Path $root "API_BIZTONSAG_HU.md") $publish -Force
Copy-Item (Join-Path $root "VALTOZASOK_0.8.7_HU.md") $publish -Force
& $InnoCompiler (Join-Path $root "installer\LocaUltrator.iss")
