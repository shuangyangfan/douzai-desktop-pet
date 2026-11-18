# 豆仔 MCP 冒烟测试（模拟 MCP 客户端：握手 + 列工具 + 调工具）
# 用法：先启动豆仔，再运行本脚本。端口自动探测。

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

Write-Host '== 1. initialize ==' -ForegroundColor Cyan
$initBody = '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}}'
Invoke-RestMethod -Method Post -Uri $base -ContentType 'application/json' -Body $initBody | ConvertTo-Json -Depth 5

Write-Host '== 2. tools/list ==' -ForegroundColor Cyan
$listBody = '{"jsonrpc":"2.0","id":2,"method":"tools/list"}'
$tools = Invoke-RestMethod -Method Post -Uri $base -ContentType 'application/json' -Body $listBody
$tools.result.tools | ForEach-Object { $_.name }

Write-Host '== 3. tools/call pet_action blush ==' -ForegroundColor Cyan
$callBody = '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"pet_action","arguments":{"action":"blush"}}}'
Invoke-RestMethod -Method Post -Uri $base -ContentType 'application/json' -Body $callBody | ConvertTo-Json -Depth 5

Write-Host '== 4. tools/call pet_chat ==' -ForegroundColor Cyan
$callBody2 = '{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"pet_chat","arguments":{"text":"Hello, 我是豆仔！"}}}'
Invoke-RestMethod -Method Post -Uri $base -ContentType 'application/json' -Body $callBody2 | ConvertTo-Json -Depth 5

Write-Host '== 5. tools/call pet_status ==' -ForegroundColor Cyan
$callBody3 = '{"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"pet_status","arguments":{}}}'
Invoke-RestMethod -Method Post -Uri $base -ContentType 'application/json' -Body $callBody3 | ConvertTo-Json -Depth 5

Write-Host 'MCP 冒烟测试完成。' -ForegroundColor Green
