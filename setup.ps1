param([string]$ApplicationDirectory = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ApplicationDirectory)) {
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'src/ReportPlatform.Web/ReportPlatform.Web.csproj')) {
        $ApplicationDirectory = Join-Path $PSScriptRoot 'src/ReportPlatform.Web'
        if (Test-Path -LiteralPath (Join-Path $PSScriptRoot '.env')) {
            Write-Host '检测到旧版配置。.NET 源码运行会读取原 .env 和 data，无需重新初始化。'
            Write-Host '请用 VS2022 打开 ReportPlatform.sln 并启动 ReportPlatform.Web。'
            exit
        }
    } else { $ApplicationDirectory = $PSScriptRoot }
}
$configPath = Join-Path $ApplicationDirectory 'appsettings.Local.json'
if (Test-Path -LiteralPath $configPath) { throw '本地配置已存在，为防止覆盖加密密钥，请直接编辑现有配置。' }
if (Test-Path -LiteralPath (Join-Path $ApplicationDirectory 'App_Data/platform.sqlite')) {
    throw '检测到已有数据库，请恢复其原有加密密钥，不要生成新密钥。'
}
$securePassword = Read-Host '设置 admin 初始密码（12–256 位）' -AsSecureString
$plainPassword = [System.Net.NetworkCredential]::new('', $securePassword).Password
if ($plainPassword.Length -lt 12 -or $plainPassword.Length -gt 256) { throw '密码长度应为 12–256 位。' }
$keyBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$encryptionKey = [System.BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant()
$config = @{ Platform = @{ EncryptionKey = $encryptionKey; AdminPassword = $plainPassword; DataDirectory = 'App_Data'; CookieSecure = $true } }
$content = $config | ConvertTo-Json -Depth 3
[System.IO.File]::WriteAllText($configPath, $content, [System.Text.UTF8Encoding]::new($false))
$plainPassword = $null
Write-Host '初始化完成。请妥善保管 appsettings.Local.json，并通过 HTTPS 部署。'
Write-Host '本机 HTTP 调试时，VS 启动配置临时关闭 CookieSecure；发布目录的 HTTP 测试需设为 false，正式部署恢复 true。'
