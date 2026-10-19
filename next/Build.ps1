param([string]$Version='0.1.0-alpha.2')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$'){throw '잘못된 버전 형식'}
$repoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts=Join-Path $repoRoot 'artifacts'
$build=Join-Path $artifacts ('build/'+[guid]::NewGuid().ToString('N'))
$app=Join-Path $build 'app'
$setup=Join-Path $build 'setup'
$release=Join-Path $artifacts ('release/'+$Version)
New-Item -ItemType Directory -Force -Path $app,$setup,$release | Out-Null
dotnet publish (Join-Path $PSScriptRoot 'Depot.Desktop/Depot.Desktop.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $app
if($LASTEXITCODE -ne 0){throw '앱 빌드 실패'}
foreach($file in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')){Copy-Item -LiteralPath (Join-Path $repoRoot $file) -Destination $app}
Copy-Item -LiteralPath (Join-Path $repoRoot 'third-party') -Destination $app -Recurse
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $app -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination (Join-Path $app '사용방법.md')
Add-Type -AssemblyName System.IO.Compression
$payload=Join-Path $PSScriptRoot 'package.zip'
$stream=[IO.File]::Create($payload)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
try{
 foreach($file in Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object Extension -ne '.pdb'){
  $name=[IO.Path]::GetRelativePath($app,$file.FullName).Replace('\','/')
  $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
  $output=$entry.Open();$input=[IO.File]::OpenRead($file.FullName)
  try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
 }
}finally{$zip.Dispose();$stream.Dispose()}
dotnet publish (Join-Path $PSScriptRoot 'Depot.Install/Depot.Install.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $setup
if($LASTEXITCODE -ne 0){throw '설치기 빌드 실패'}
$portable=Join-Path $release "BMSDepot-$Version-win-x64.zip"
$installer=Join-Path $release "BMSDepot-$Version-win-x64-setup.exe"
Copy-Item -LiteralPath $payload -Destination $portable -Force
Copy-Item -LiteralPath (Join-Path $setup 'BMSDepotNext-Setup.exe') -Destination $installer -Force
$hashes=foreach($file in @($portable,$installer)){'{0}  {1}' -f (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant(),[IO.Path]::GetFileName($file)}
$hashes | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding ascii
Write-Output "릴리스 파일: $release"
