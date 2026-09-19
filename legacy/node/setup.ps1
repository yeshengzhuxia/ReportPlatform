$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
if (Test-Path -LiteralPath '.env') { Write-Host '配置文件已存在，无需重新初始化。'; exit }
$securePassword = Read-Host '设置 admin 密码（至少 12 位）' -AsSecureString
$plainPassword = [System.Net.NetworkCredential]::new('', $securePassword).Password
if ($plainPassword.Length -lt 12 -or $plainPassword.Contains('"') -or $plainPassword.Contains("`n") -or $plainPassword.Contains("`r")) { throw '密码需至少 12 位，不能包含双引号或换行。' }
$keyBytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$rng.GetBytes($keyBytes)
$rng.Dispose()
$encryptionKey = [System.BitConverter]::ToString($keyBytes).Replace('-', '').ToLowerInvariant()
$configLines = @('PORT=3080', 'HOST=127.0.0.1', ('ADMIN_PASSWORD="' + $plainPassword + '"'), ('ENCRYPTION_KEY=' + $encryptionKey), 'COOKIE_SECURE=false')
[System.IO.File]::WriteAllLines((Join-Path $PSScriptRoot '.env'), $configLines, [System.Text.UTF8Encoding]::new($false))
$plainPassword = $null
Write-Host '初始化完成。运行 npm start 启动平台，然后访问 http://127.0.0.1:3080。'
