param([int]$Port = 5091)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publishDirectory = Join-Path $projectRoot 'artifacts/publish'
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'ReportPlatform.Web.dll'))) { throw '请先发布应用。' }
$smokeDirectory = Join-Path $projectRoot ('artifacts/smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeDirectory | Out-Null
$variables = @('Platform__DataDirectory','Platform__EncryptionKey','Platform__AdminPassword','Platform__CookieSecure','ASPNETCORE_ENVIRONMENT')
$previous = @{}
foreach ($name in $variables) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$process = $null
try {
    $env:Platform__DataDirectory = $smokeDirectory
    $env:Platform__EncryptionKey = 'a' * 64
    $env:Platform__AdminPassword = 'PublishSmoke123!'
    $env:Platform__CookieSecure = 'false'
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    $process = Start-Process -FilePath 'dotnet' -ArgumentList @('ReportPlatform.Web.dll', '--urls', "http://127.0.0.1:$Port") -WorkingDirectory $publishDirectory -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $smokeDirectory 'stdout.log') -RedirectStandardError (Join-Path $smokeDirectory 'stderr.log')
    $url = "http://127.0.0.1:$Port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if ($process.HasExited) { throw '发布程序提前退出，请查看 smoke 目录日志。' }
        try { $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 2; if ($response.StatusCode -eq 200) { $ready = $true; break } } catch { Start-Sleep -Milliseconds 250 }
    }
    if (-not $ready) { throw '发布程序未能正常响应。' }
    $headers = @{ 'X-Requested-With' = 'ReportPlatform' }
    $login = @{ username = 'admin'; password = 'PublishSmoke123!'; accountId = 'default' } | ConvertTo-Json
    $null = Invoke-RestMethod -Uri "$url/api/login" -Method Post -Body $login -ContentType 'application/json' -Headers $headers -SessionVariable smokeSession
    $bootstrap = Invoke-RestMethod -Uri "$url/api/bootstrap" -WebSession $smokeSession
    if (-not $bootstrap.admin -or $bootstrap.account.orgId -ne '1') { throw '发布版登录或账套验证失败。' }
    foreach ($asset in @('app.js','style.css','favicon.svg')) {
        if ((Invoke-WebRequest -Uri "$url/$asset" -UseBasicParsing).StatusCode -ne 200) { throw "静态资源缺失：$asset" }
    }
    $privateFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -Force | Where-Object { $_.Name -in @('.env','platform.sqlite','appsettings.Local.json','首次登录.txt') }
    if ($privateFiles) { throw '发布目录包含不应打包的私有配置。' }
    Write-Output '发布产物验证通过：独立启动、静态资源、登录、账套、管理员权限、私有配置排除。'
} finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id; $process.WaitForExit() }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
