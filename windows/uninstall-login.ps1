#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位管理员 PowerShell。' }
$providerId = '{18CC018C-6398-4E25-8407-3D4D897E6F30}'
$classPath = 'SOFTWARE\Classes\CLSID\' + $providerId
$providerPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\' + $providerId
$root = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'WatchUnlock\CredentialProvider')) + '\'
$server = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($classPath + '\InprocServer32')
try {
    if ($server -and -not [IO.Path]::GetFullPath([string]$server.GetValue('')).StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw '登录入口指向其他目录，请先确认。' }
} finally { if ($server) { $server.Dispose() } }
[Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree($providerPath, $false)
[Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree($classPath, $false)
Write-Host 'WatchUnlock 登录入口已移除。文件和配置保留；需要删除密码时，在部署配置中选择 8。'
