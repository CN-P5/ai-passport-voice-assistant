param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\AiPassportVoice.App\AiPassportVoice.App.csproj"
$out  = Join-Path $PSScriptRoot "publish\$Runtime"

Write-Host "发布 $proj -> $out"

dotnet publish $proj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "发布失败，退出码 $LASTEXITCODE" }

$exe = Join-Path $out "AiPassportVoice.exe"
Write-Host ""
Write-Host "完成: $exe"
if (Test-Path $exe) {
    $size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
    Write-Host "单文件大小: $size MB（自包含，无需安装 .NET 运行时）"
}
