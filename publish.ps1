$ErrorActionPreference = "Stop"

$root = [System.IO.Path]::GetFullPath($PSScriptRoot)
$toolsDirectory = Join-Path $root "obj\publish-tools"
$githubCliArchive = Join-Path $toolsDirectory "gh-windows-x64.zip"
$minGitArchive = Join-Path $toolsDirectory "mingit-windows-x64.zip"
$githubCliExtracted = Join-Path $toolsDirectory "gh-extracted"
$publishDirectory = Join-Path $root "obj\publish-win-x64"
$bundleToolsDirectory = Join-Path $toolsDirectory "bundle"
$executableName = "MinecraftSaves-Windows-x64.exe"
$publishExecutable = Join-Path $publishDirectory $executableName
$downloadExecutable = Join-Path $root $executableName

New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $bundleToolsDirectory -Force | Out-Null

$githubCliUrl = "https://github.com/cli/cli/releases/download/v2.102.0/gh_2.102.0_windows_amd64.zip"
$githubCliSha256 = "AE64E556ECC240B200F7EBA60D550E4BB60D78E860E69DD88C449405B86067F4"
$minGitUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.1/MinGit-2.56.0-64-bit.zip"

Write-Host "Descargando GitHub CLI oficial..."
if (-not (Test-Path -LiteralPath $githubCliArchive)) {
    Invoke-WebRequest -Uri $githubCliUrl -OutFile $githubCliArchive
}
$actualGithubCliSha256 = (Get-FileHash -LiteralPath $githubCliArchive -Algorithm SHA256).Hash
if ($actualGithubCliSha256 -ne $githubCliSha256) {
    throw "La verificación SHA-256 de GitHub CLI no coincide."
}

Write-Host "Descargando Git for Windows portátil..."
if (-not (Test-Path -LiteralPath $minGitArchive)) {
    Invoke-WebRequest -Uri $minGitUrl -OutFile $minGitArchive
}

if (Test-Path -LiteralPath $githubCliExtracted) {
    Remove-Item -LiteralPath $githubCliExtracted -Recurse -Force
}
Expand-Archive -LiteralPath $githubCliArchive -DestinationPath $githubCliExtracted -Force
$githubCliExecutable = Get-ChildItem -LiteralPath $githubCliExtracted -Filter "gh.exe" -File -Recurse |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $githubCliExecutable) {
    throw "El paquete oficial no contiene gh.exe."
}
Copy-Item -LiteralPath $githubCliExecutable -Destination (Join-Path $bundleToolsDirectory "gh.exe") -Force
Copy-Item -LiteralPath $minGitArchive -Destination (Join-Path $bundleToolsDirectory "mingit.zip") -Force

Write-Host "Publicando la aplicación autocontenida para Windows x64..."
dotnet publish (Join-Path $root "MinecraftSaves.csproj") `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:AssemblyName=MinecraftSaves-Windows-x64 `
    "-p:BundleToolsDirectory=$bundleToolsDirectory"
if ($LASTEXITCODE -ne 0) {
    throw "La publicación autocontenida falló."
}
if (-not (Test-Path -LiteralPath $publishExecutable)) {
    throw "No se generó el ejecutable descargable."
}

Copy-Item -LiteralPath $publishExecutable -Destination $downloadExecutable -Force
$size = (Get-Item -LiteralPath $downloadExecutable).Length
Write-Host ("Listo: {0} ({1:N0} MB)" -f $downloadExecutable, ($size / 1MB))
Write-Host "El primer inicio instala la aplicación y sus herramientas en tu perfil de Windows."
