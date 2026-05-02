
# Recents Publish Script
# Optimized for a premium, single-file, self-contained experience.

$projectName = "Recents.App"
$projectPath = "src/$projectName/$projectName.csproj"
$outputDir = "src/$projectName/bin/Release/net10.0-windows/win-x64/publish"

Write-Host "Cleaning previous builds..." -ForegroundColor Cyan
dotnet clean -c Release

Write-Host "Publishing $projectName..." -ForegroundColor Cyan
dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishReadyToRun=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $outputDir

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nSuccessfully published to: $outputDir" -ForegroundColor Green
    Write-Host "Executable: $outputDir\Recents.exe" -ForegroundColor Green
} else {
    Write-Host "`nPublish failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}
