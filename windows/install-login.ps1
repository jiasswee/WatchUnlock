#Requires -RunAsAdministrator
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not [Environment]::Is64BitProcess) { throw '请使用 64 位管理员 PowerShell。' }
$providerId = '{18CC018C-6398-4E25-8407-3D4D897E6F30}'
$classPath = 'SOFTWARE\Classes\CLSID\' + $providerId
$providerPath = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\' + $providerId
$source = Join-Path $PSScriptRoot 'credential-provider\WatchUnlock.CredentialProvider.dll'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw '请解压完整的 Windows 构建产物。' }
if ((Get-Service -Name WatchUnlock).Status -ne 'Running') { throw '请先安装并启动 WatchUnlock 服务。' }
$settings = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\WatchUnlock')
try {
    if (-not $settings -or $settings.GetValue('LoginEnabled', 0) -ne 1) { throw '请先在部署配置中设置手机、保存正确的本地账户密码，并启用 WatchUnlock。' }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { if ($identity.User.Value -ne $settings.GetValue('AccountSid', '')) { throw '请在已配置账户的桌面上，以该账户的管理员权限安装登录入口。' } }
    finally { $identity.Dispose() }
} finally { if ($settings) { $settings.Dispose() } }
$root = Join-Path $env:ProgramFiles 'WatchUnlock\CredentialProvider'
$version = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.Substring(0, 16)
$targetFolder = Join-Path $root $version
$target = Join-Path $targetFolder 'WatchUnlock.CredentialProvider.dll'
$existing = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey($classPath + '\InprocServer32')
try {
    if ($existing) {
        $old = [IO.Path]::GetFullPath([string]$existing.GetValue(''))
        if (-not $old.StartsWith([IO.Path]::GetFullPath($root) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw '该登录入口标识已指向其他目录，请先确认。' }
    }
} finally { if ($existing) { $existing.Dispose() } }
foreach ($folder in @((Join-Path $env:ProgramFiles 'WatchUnlock'), $root, $targetFolder)) {
    if ((Test-Path -LiteralPath $folder) -and (Get-Item -LiteralPath $folder).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw '安装目录不能使用链接。' }
}
New-Item -ItemType Directory -Path $targetFolder -Force | Out-Null
if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $source -Destination $target }
$class = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($classPath)
try { $class.SetValue('', 'WatchUnlock Credential Provider') } finally { $class.Dispose() }
$server = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($classPath + '\InprocServer32')
try { $server.SetValue('', $target); $server.SetValue('ThreadingModel', 'Both') } finally { $server.Dispose() }
$provider = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey($providerPath)
try { $provider.SetValue('', 'WatchUnlock') } finally { $provider.Dispose() }
Write-Host 'WatchUnlock 登录入口已安装。下一次锁屏，在登录选项中选择“使用手表解锁”。'
Write-Host '更新 DLL 后若仍显示旧版本，请重启电脑。卸载入口可运行 uninstall-login.ps1。'
