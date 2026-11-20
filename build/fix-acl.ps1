# 修复目录完整性级别（Integrity Level）。
# 症状：目录被沙箱类工具标成 Low Mandatory Level 后，普通权限双击 exe 会报 0xC0000142，
#       但管理员能运行、复制到其他目录也能运行。
# 用法：右键「使用 PowerShell 运行」，或：
#   powershell -ExecutionPolicy Bypass -File build\fix-acl.ps1 -Path "D:\AI文档等部署\AI-passport语音"

param(
    [string]$Path = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $Path)) {
    Write-Error "路径不存在: $Path"
    exit 1
}

Write-Host "目标目录: $Path"
Write-Host "修复前:"
icacls $Path | Select-String "Mandatory"

Write-Host "正在设置完整性级别为 Medium（递归）..."
icacls $Path /setintegritylevel "(OI)(CI)Medium" /T | Select-Object -Last 2

Write-Host "修复后:"
icacls $Path | Select-String "Mandatory"

Write-Host ""
Write-Host "完成。请重新双击程序测试。"
