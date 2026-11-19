#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

$sourceFolder = Join-Path $PSScriptRoot 'service'
$sourceExecutable = Join-Path $sourceFolder 'WatchUnlock.Service.exe'
if (-not (Test-Path -LiteralPath $sourceExecutable -PathType Leaf)) {
    throw '请解压完整的 Windows 构建产物，再从产物根目录运行此脚本。'
}
$targetFolder = Join-Path $env:ProgramFiles 'WatchUnlock\Service'
$targetExecutable = Join-Path $targetFolder 'WatchUnlock.Service.exe'
$binaryPath = '"' + $targetExecutable + '"'
$existing = Get-CimInstance Win32_Service -Filter "Name='WatchUnlock'"
if ($existing) {
    if ($existing.PathName -ne $binaryPath) { throw '已存在指向其他目录的 WatchUnlock 服务，请先确认其用途。' }
    $service = Get-Service -Name WatchUnlock
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name WatchUnlock
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
}
New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
Get-ChildItem -LiteralPath $sourceFolder | Copy-Item -Destination $targetFolder -Recurse -Force
if (-not $existing) {
    New-Service -Name WatchUnlock -BinaryPathName $binaryPath -DisplayName 'WatchUnlock BLE Authorization' -StartupType Automatic | Out-Null
}
Start-Service -Name WatchUnlock
Write-Host '后台服务已安装并启动。以管理员身份打开 WatchUnlock.BleClient.exe，输入 s 设置手机。'
Write-Host '本阶段提供后台授权和自动回连，尚未接入 Windows 登录。'
