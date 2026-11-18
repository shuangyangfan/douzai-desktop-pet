<#
  Douzai - 3D Desktop Pet / 豆仔 · 一键编译并启动

  只用 Windows 自带 .NET Framework 4.x 里的 csc.exe，不需要安装任何 SDK。
  同时兼容 Windows PowerShell 5.1 与 PowerShell 7+。
  本文件必须保存为「UTF-8 带 BOM」，否则 PowerShell 5.1 会按 ANSI 读取中文而报解析错误。
#>
$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $dir 'pet.cs'
$exe = Join-Path $dir 'DesktopPet.exe'

# 1. 定位编译器
$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) {
    Write-Host '找不到 csc.exe：本项目需要 Windows 自带的 .NET Framework 4.x。' -ForegroundColor Red
    exit 1
}

# 2. 定位 WPF 程序集
#    优先用 csc.exe 同目录的程序集副本，避免与 GAC 里的同名程序集重复引用（CS1703）
$assemblyNames = @('PresentationCore', 'PresentationFramework', 'WindowsBase', 'System.Xaml', 'System.Windows.Forms', 'System.Drawing')
$gacRoots = @(
    "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL",
    "$env:WINDIR\Microsoft.NET\assembly\GAC_64",
    "$env:WINDIR\assembly\GAC_MSIL",
    "$env:WINDIR\assembly\GAC_64"
)
$refs = @()
foreach ($n in $assemblyNames) {
    $local = Join-Path (Split-Path -Parent $csc) "$n.dll"
    if (Test-Path $local) { $refs += "/r:$local"; continue }

    $hit = $null
    foreach ($root in $gacRoots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem -Path (Join-Path $root $n) -Recurse -Filter "$n.dll" -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($hit) { break }
    }
    if ($hit) { $refs += "/r:$($hit.FullName)" }
    else {
        Write-Host "缺少程序集：$n" -ForegroundColor Red
        exit 1
    }
}

# 3. 编译
#    参数里不要自己加引号：PowerShell 5.1 与 7 对带引号的原生参数处理不一样，
#    手动加引号会让 csc 收到「带引号的路径」，报 CS0006（元数据文件找不到）。
Write-Host '正在编译…' -ForegroundColor Cyan
$argList = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe") + $refs + @($src)
& $csc @argList

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exe)) {
    Write-Host '编译失败。若豆仔正在运行，请先从托盘菜单退出，再重新运行本脚本。' -ForegroundColor Red
    exit 1
}

# 4. 启动
#    刚写出的 exe 偶尔会被杀软/索引服务短暂锁住，直接 Start-Process 会报「拒绝访问」，重试几次
Write-Host '编译成功，启动豆仔…' -ForegroundColor Green
$started = $false
for ($i = 0; $i -lt 6; $i++) {
    try {
        Start-Process -FilePath $exe
        $started = $true
        break
    } catch {
        Start-Sleep -Milliseconds 400
    }
}
if (-not $started) {
    Write-Host "编译成功，但启动失败：$exe" -ForegroundColor Yellow
    Write-Host '可以直接双击这个 exe 试试。' -ForegroundColor Yellow
    exit 1
}