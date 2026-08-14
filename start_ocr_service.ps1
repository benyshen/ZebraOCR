# ============================================================
# start_ocr_service.ps1 - ZebraOCR OCR 服务快速启动脚本
# ------------------------------------------------------------
# 功能:
#   1. 自动检测 Python / GPU / 模型目录
#   2. 一键启动 OCR 服务 (Unlimited-OCR 或 PaddleOCR-VL-0.9B)
#   3. 端口冲突时自动复用已运行的服务，或用 -Restart 强制重启
#   4. 等待模型加载完成，可选 -Test 对样例图片做一次识别自测
#
# 用法示例:
#   .\start_ocr_service.ps1                        # 交互菜单选择模型
#   .\start_ocr_service.ps1 -Model unlimited       # 启动 Unlimited-OCR (端口 5100)
#   .\start_ocr_service.ps1 -Model paddleocr-vl    # 启动 PaddleOCR-VL-0.9B (端口 5101)
#   .\start_ocr_service.ps1 -Model paddleocr-vl -Restart -Test
#                                                  # 强制重启 VL 服务并自测识别
# ============================================================
[CmdletBinding()]
param(
    [ValidateSet('unlimited', 'paddleocr-vl', 'auto')]
    [string]$Model,
    [int]$Port = 0,
    [switch]$Restart,
    [switch]$Test,
    [string]$Image = '',
    [int]$MaxLength = 0,
    [int]$TimeoutSec = 600,
    [switch]$ForceCPU,
    [switch]$NoPrompt
)

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition

# UTF-8 输出（防止中文乱码）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
$OutputEncoding = [System.Text.Encoding]::UTF8

$UNLIMITED_DIR = Join-Path $root 'Unlimited-OCR'
$PADDLEOCR_DIR = Join-Path $root 'PaddleOCR-VL-0.9B'

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-OK($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn($msg) { Write-Host "    $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "    $msg" -ForegroundColor Red }

function Find-Python {
    $candidates = @(
        'C:\Python314\python.exe',
        'C:\Python313\python.exe',
        'C:\Python312\python.exe',
        'C:\Python311\python.exe',
        'C:\Python310\python.exe',
        'C:\Program Files\Python314\python.exe',
        'C:\Program Files\Python313\python.exe',
        'C:\Program Files\Python312\python.exe',
        "$env:LOCALAPPDATA\Programs\Python\Python314\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python313\python.exe",
        "$env:LOCALAPPDATA\Programs\Python\Python312\python.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
    $cmd = Get-Command python -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Stop-PortOwner([int]$p) {
    $conns = Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue
    foreach ($c in $conns) {
        try {
            Write-Warn "结束占用端口 $p 的进程 PID $($c.OwningProcess)"
            Stop-Process -Id $c.OwningProcess -Force -ErrorAction Stop
        } catch { Write-Warn "结束进程失败: $($_.Exception.Message)" }
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Milliseconds 500
    }
    Write-Err "端口 $p 仍被占用，无法释放"
}

# ---------- 1. 选择模型 ----------
Write-Step "ZebraOCR OCR 服务快速启动"
if (-not $Model) {
    if ($NoPrompt -or $Host.Name -ne 'ConsoleHost') {
        $Model = 'unlimited'
    } else {
        Write-Host "  选择要启动的 OCR 模型:"
        Write-Host "    1) Unlimited-OCR      (默认, 端口 5100)"
        Write-Host "    2) PaddleOCR-VL-0.9B  (端口 5101)"
        $choice = Read-Host "  请输入 1 或 2 (直接回车 = 1)"
        $Model = if ($choice -eq '2') { 'paddleocr-vl' } else { 'unlimited' }
    }
}

switch ($Model) {
    'paddleocr-vl' { $modelPath = $PADDLEOCR_DIR; $defaultPort = 5101 }
    'unlimited'    { $modelPath = $UNLIMITED_DIR; $defaultPort = 5100 }
    'auto' {
        if (Test-Path (Join-Path $UNLIMITED_DIR 'modeling_unlimitedocr.py')) {
            $Model = 'unlimited'; $modelPath = $UNLIMITED_DIR; $defaultPort = 5100
        } elseif (Test-Path (Join-Path $PADDLEOCR_DIR 'modeling_paddleocr_vl.py')) {
            $Model = 'paddleocr-vl'; $modelPath = $PADDLEOCR_DIR; $defaultPort = 5101
        } else {
            $Model = 'unlimited'; $modelPath = $UNLIMITED_DIR; $defaultPort = 5100
        }
    }
}
if ($Port -le 0) { $Port = $defaultPort }

Write-OK "模型类型: $Model"
Write-OK "模型目录: $modelPath"
Write-OK "服务端口: $Port"

# ---------- 2. 检查模型目录 ----------
if (-not (Test-Path $modelPath)) {
    Write-Err "模型目录不存在: $modelPath"
    Write-Host ""
    $q = '"'
    Write-Host "  请先下载模型（开源，HuggingFace）:"
    Write-Host ('    Unlimited-OCR:    huggingface-cli download baidu/Unlimited-OCR --local-dir ' + $q + $UNLIMITED_DIR + $q)
    Write-Host ('    PaddleOCR-VL-0.9B: huggingface-cli download lvyufeng/PaddleOCR-VL-0.9B --local-dir ' + $q + $PADDLEOCR_DIR + $q)
    exit 1
}

# ---------- 3. 定位 Python 与服务脚本 ----------
$python = Find-Python
if (-not $python) {
    Write-Err "未找到 Python，请安装 Python 3.12~3.14 并加入 PATH"
    exit 1
}
Write-OK "Python: $python"

if ($Model -eq 'paddleocr-vl') {
    $serverCandidates = @(
        (Join-Path $root 'ZebraOCR/ZebraOCR/ocr_server_paddleocr_vl.py'),
        (Join-Path $root 'ocr_server_paddleocr_vl.py')
    )
} else {
    $serverCandidates = @(
        (Join-Path $root 'ZebraOCR/ZebraOCR/ocr_server_unlimited.py'),
        (Join-Path $root 'ocr_server_unlimited.py')
    )
}
$serverScript = $serverCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $serverScript) {
    Write-Err "未找到服务脚本，请确认 ZebraOCR 工程完整: $($serverCandidates[0])"
    exit 1
}
Write-OK "服务脚本: $serverScript"

# ---------- 4. 环境预检 (torch / transformers / GPU) ----------
Write-Step "环境预检"
$pyLines = @(
    'import sys',
    'import torch, transformers',
    'print(''TORCH_OK'', torch.__version__)',
    'print(''CUDA'', torch.cuda.is_available())',
    'if torch.cuda.is_available():',
    '    print(''GPU'', torch.cuda.get_device_name(0))',
    '    free = torch.cuda.mem_get_info(0)[0] / 1024**3',
    '    print(''VRAM_FREE_GB'', round(free, 2))',
    'print(''TRANSFORMERS'', transformers.__version__)'
)
$pyCode = $pyLines -join [char]10
$preflightText = (& $python -c $pyCode 2>&1 | Out-String).Trim()
Write-Host "    $preflightText" -ForegroundColor Gray
if ($LASTEXITCODE -ne 0 -or $preflightText -notmatch 'TORCH_OK') {
    Write-Err "PyTorch/transformers 未安装或不可用，请执行:"
    Write-Err "  pip install torch==2.11.0 torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128"
    Write-Err "  pip install transformers sentencepiece"
    exit 1
}

if ($Model -eq 'paddleocr-vl') {
    $sp = (& $python -c "import sentencepiece; print('SP_OK')" 2>&1 | Out-String)
    if ($sp -notmatch 'SP_OK') {
        Write-Err "缺少 sentencepiece（PaddleOCR-VL tokenizer 必需），请执行: pip install sentencepiece"
        exit 1
    }
    Write-OK "sentencepiece: 已安装"
}

# ---------- 5. 端口检查 / 复用 / 重启 ----------
$listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
$proc = $null
$reused = $false
$healthObj = $null
if ($listener) {
    $healthOk = $false
    try {
        $healthObj = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 5
        $healthOk = ($healthObj.status -eq 'ok' -and $healthObj.model_loaded -eq $true)
    } catch { }
    if ($healthOk -and -not $Restart) {
        Write-OK "端口 $Port 已有运行中的 OCR 服务 (PID $($listener.OwningProcess))，直接复用"
        Write-OK "设备: $($healthObj.device) | 模型: $($healthObj.model_type)"
        $reused = $true
    } else {
        if ($healthOk) { Write-Warn "-Restart 已指定，重启端口 $Port 上的服务" }
        else { Write-Warn "端口 $Port 被占用 (PID $($listener.OwningProcess)) 但健康检查失败（可能正在处理超长请求或已卡死），将强制重启" }
        Stop-PortOwner $Port
    }
}

# ---------- 6. 启动服务 ----------
if (-not $reused) {
    $outLog = Join-Path $root ('ocr_server_' + $Port + '_out.log')
    $errLog = Join-Path $root ('ocr_server_' + $Port + '_err.log')
    Remove-Item $outLog, $errLog -ErrorAction SilentlyContinue

    Write-Step "启动 OCR 服务 (后台, 隐藏窗口)"
    $extra = ''
    if ($ForceCPU) { $extra = ' --force-cpu' }
    $argList = '"' + $serverScript + '" --port ' + $Port + ' --model "' + $modelPath + '"' + $extra
    $inFile = Join-Path $root '_null_in.txt'
    if (-not (Test-Path $inFile)) { [IO.File]::WriteAllText($inFile, '', [Text.Encoding]::UTF8) }
    $proc = Start-Process -FilePath $python -ArgumentList $argList -WindowStyle Hidden -RedirectStandardOutput $outLog -RedirectStandardError $errLog -RedirectStandardInput $inFile -PassThru
    Write-OK "已启动 PID $($proc.Id)"
    Write-OK "日志: $outLog / $errLog"

    Write-Step "等待模型加载 (超时 ${TimeoutSec}s) ..."
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $loaded = $false
    while ((Get-Date) -lt $deadline) {
        if ($proc.HasExited) {
            Write-Err "服务进程已退出 (退出码 $($proc.ExitCode))，错误日志尾部:"
            if (Test-Path $errLog) { Get-Content $errLog -Tail 25 }
            exit 4
        }
        try {
            $healthObj = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 3
            if ($healthObj.status -eq 'ok' -and $healthObj.model_loaded -eq $true) { $loaded = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $loaded) {
        Write-Err "等待模型加载超时，错误日志尾部:"
        if (Test-Path $errLog) { Get-Content $errLog -Tail 25 }
        Write-Err "提示: 首次加载可能较慢；若显存不足可加 -ForceCPU；若一直失败请查看日志"
        exit 5
    }
}

Write-OK "OCR 服务就绪: http://127.0.0.1:$Port  (设备: $($healthObj.device), 模型: $($healthObj.model_type))"

# ---------- 7. 可选: 自测识别 ----------
if ($Test) {
    if (-not $Image) {
        foreach ($cand in @('OCR_Sample.jpg', 'OCR_Sample.png', 'OCR_Sample_std.jpg')) {
            $p = Join-Path $root $cand
            if (Test-Path $p) { $Image = $p; break }
        }
    }
    if (-not $Image -or -not (Test-Path $Image)) {
        Write-Err "测试图片不存在，请用 -Image 指定图片路径"
        exit 6
    }
    if ($MaxLength -le 0) {
        $MaxLength = if ($Model -eq 'paddleocr-vl') { 512 } else { 2048 }
    }
    $prompt = '<image>Extract and transcribe all text visible in this image. Preserve layout and line breaks.'
    $body = @{
        image_path = $Image
        prompt     = $prompt
        image_mode = 'gundam'
        max_length = $MaxLength
    } | ConvertTo-Json

    Write-Step "开始 OCR 自测 (max_length=$MaxLength)，首次推理可能需要几分钟，请稍候..."
    Write-OK "测试图片: $Image"
    $t0 = Get-Date
    try {
        $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/recognize" -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 1200
        $secs = ((Get-Date) - $t0).TotalSeconds
        if ($resp.success) {
            $resultFile = Join-Path $root ('ocr_result_' + $Port + '.txt')
            $resp.result | Set-Content -Path $resultFile -Encoding UTF8
            Write-OK "识别成功，用时 $([math]::Round($secs, 1)) 秒"
            Write-Host "---------- OCR 结果 ----------"
            Write-Host $resp.result
            Write-Host "------------------------------"
            Write-OK "结果已保存: $resultFile"
        } else {
            Write-Err "识别失败: $($resp.error)"
            exit 7
        }
    } catch {
        Write-Err "OCR 测试请求失败: $($_.Exception.Message)"
        Write-Err "提示: 若超时，PaddleOCR-VL 可加 -MaxLength 512 减小生成长度再试"
        exit 8
    }
}

# ---------- 8. 完成 ----------
Write-Step "完成"
Write-Host ""
Write-Host "  服务地址: http://127.0.0.1:$Port"
Write-Host "  健康检查: Invoke-RestMethod http://127.0.0.1:$Port/health"
Write-Host '  手动识别: $body = @{ image_path=''D:\x.jpg''; prompt=''<image>document parsing.''; image_mode=''gundam''; max_length=2048 } | ConvertTo-Json'
Write-Host ('            Invoke-RestMethod http://127.0.0.1:' + $Port + '/recognize -Method Post -ContentType ''application/json'' -Body $body')
Write-Host ""
Write-Host "  现在可以直接启动 ZebraOCR.exe 使用（程序会自动复用此服务）。"
