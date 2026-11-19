#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name WatchUnlock -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name WatchUnlock
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete WatchUnlock
    if ($LASTEXITCODE -ne 0) { throw '删除服务失败，请查看上面的 Windows 错误。' }
}
Write-Host 'WatchUnlock 后台服务已移除。已保存的配置和程序文件可供重新安装使用。'
