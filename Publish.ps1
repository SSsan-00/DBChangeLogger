param([switch]$DemoOnly)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$output = Join-Path $PSScriptRoot 'artifacts\release\windows'
$encrypted = Join-Path $output 'connections.enc'
if (-not $DemoOnly) {
    if (-not (Test-Path -LiteralPath 'Configure\PrivateConnections.local.cs')) {
        throw 'Configure\PrivateConnections.example.txt を PrivateConnections.local.cs としてコピーし、接続文字列を入力してください。'
    }
    dotnet run --project Configure\Configure.csproj -c Release -- $encrypted
    if ($LASTEXITCODE -ne 0) { throw '暗号化設定の生成に失敗したため、配布ZIPは更新していません。' }
} elseif (Test-Path -LiteralPath $encrypted) {
    Remove-Item -LiteralPath $encrypted
}
dotnet publish App\App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Windowsビルドに失敗しました。' }
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'App\bin\Release\net9.0-windows\win-x64\Microsoft.Data.SqlClient.SNI.dll') -Destination $output -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'INSTALL.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'WINDOWS-VERIFICATION.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'BENCHMARK-100M.md') -Destination $output
$windowsFiles = @('DBChangeLogger.exe', 'Microsoft.Data.SqlClient.SNI.dll', 'README.md', 'INSTALL.md', 'WINDOWS-VERIFICATION.md', 'BENCHMARK-100M.md') | ForEach-Object { Join-Path $output $_ }
if (-not $DemoOnly) { $windowsFiles += $encrypted }
Compress-Archive -LiteralPath $windowsFiles -DestinationPath (Join-Path $PSScriptRoot 'artifacts\DBChangeLogger-Windows-x64.zip') -Force

# 共有可能なファイルだけを列挙する。ローカル設定、bin/obj、テスト生成物は含めない。
$sourceFiles = @(
    'App\App.csproj', 'App\Program.cs',
    'Core\Core.csproj', 'Core\Engine.cs', 'Core\DemoEvidence.cs', 'Core\ConnectionSettings.cs', 'Core\TableCatalog.cs', 'Core\SessionStore.cs',
    'Configure\Configure.csproj', 'Configure\Program.cs', 'Configure\PrivateConnections.example.txt',
    'Tests\Tests.csproj', 'Tests\DatabaseIntegrationTests.cs', 'Tests\EvidenceTests.cs', 'Tests\ExcelArtifactTests.cs', 'Tests\ConnectionSettingsTests.cs', 'Tests\LargeTableTests.cs', 'Tests\TableCatalogTests.cs', 'Tests\SessionStoreTests.cs',
    'README.md', 'INSTALL.md', 'WINDOWS-VERIFICATION.md', 'BENCHMARK-100M.md', 'test-databases.sh', 'Publish.ps1', 'global.json', '.gitignore'
)
Add-Type -AssemblyName System.IO.Compression
$archivePath = Join-Path $PSScriptRoot 'artifacts\DBChangeLogger-Source.zip'
$archiveStream = [System.IO.File]::Open($archivePath, [System.IO.FileMode]::Create)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($archiveStream, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $sourceFiles) {
            $entry = $archive.CreateEntry(('DBChangeLogger/' + $relative.Replace('\', '/')))
            $entryStream = $entry.Open()
            $inputStream = [System.IO.File]::OpenRead((Join-Path $PSScriptRoot $relative))
            try { $inputStream.CopyTo($entryStream) } finally { $inputStream.Dispose(); $entryStream.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $archiveStream.Dispose() }
Write-Output 'artifactsにWindows配布ZIPとソースZIPを作成しました。'
