# 豆仔 REST 接口冒烟测试
# 用法：先启动豆仔，再运行本脚本。端口会自动探测（8787 起，被占用会 +1）

$base = $null
foreach ($p in 8787, 8788, 8789) {
    try {
        $r = Invoke-RestMethod "http://127.0.0.1:$p/pet/port" -TimeoutSec 2
        $base = "http://127.0.0.1:$($r.data.port)"
        break
    } catch { }
}
if (-not $base) {
    Write-Host '没找到豆仔。请先运行 run.cmd 或 DesktopPet.exe。' -ForegroundColor Red
    exit 1
}
Write-Host "端口: $base" -ForegroundColor DarkGray
$hdr = @{ 'Content-Type' = 'application/json' }

Write-Host '== GET /pet/port ==' -ForegroundColor Cyan
Invoke-RestMethod "$base/pet/port"

Write-Host '== GET /status ==' -ForegroundColor Cyan
Invoke-RestMethod "$base/status"

Write-Host '== POST /pet/chat（说话气泡） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/chat" -Headers $hdr -Body '{"text":"你好呀，我是豆仔！"}'
Start-Sleep -Seconds 1

Write-Host '== POST /pet/action blush（害羞） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/action" -Headers $hdr -Body '{"action":"blush"}'
Start-Sleep -Seconds 1

Write-Host '== POST /pet/action shake（摇头） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/action" -Headers $hdr -Body '{"action":"shake"}'
Start-Sleep -Seconds 2

Write-Host '== POST /pet/action turn（转圈） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/action" -Headers $hdr -Body '{"action":"turn"}'
Start-Sleep -Seconds 1

Write-Host '== POST /pet/action bounce（弹跳） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/action" -Headers $hdr -Body '{"action":"bounce"}'
Start-Sleep -Seconds 1

Write-Host '== POST /pet/move（平滑移动到屏幕坐标，单位 DIP） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/move" -Headers $hdr -Body '{"x":600,"y":400}'
Start-Sleep -Seconds 1

Write-Host '== POST /pet/face（转正面向镜头） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/face" -Headers $hdr

Write-Host '== POST /pet/visible false / true（隐藏再显示） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/visible" -Headers $hdr -Body '{"show":false}'
Start-Sleep -Seconds 1
Invoke-RestMethod -Method Post "$base/pet/visible" -Headers $hdr -Body '{"show":true}'

Write-Host '== POST /pet/mood（六种情绪） ==' -ForegroundColor Cyan
foreach ($m in @('happy', 'shy', 'curious', 'sleepy', 'doze', 'neutral')) {
    Invoke-RestMethod -Method Post "$base/pet/mood" -Headers $hdr -Body ('{"mood":"' + $m + '"}')
    Start-Sleep -Milliseconds 500
}

Write-Host '== POST /pet/mood 非法值（应返回 code=1，且进程存活） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/mood" -Headers $hdr -Body '{"mood":"angry"}'

Write-Host '== POST /pet/pause（暂停再恢复闲逛） ==' -ForegroundColor Cyan
Invoke-RestMethod -Method Post "$base/pet/pause" -Headers $hdr -Body '{"on":true}'
Start-Sleep -Milliseconds 600
Invoke-RestMethod -Method Post "$base/pet/pause" -Headers $hdr -Body '{"on":false}'

Write-Host '== GET /status（收尾确认） ==' -ForegroundColor Cyan
Invoke-RestMethod "$base/status"

Write-Host '全部接口调用完成。' -ForegroundColor Green
