@echo off
chcp 936 >nul
setlocal EnableExtensions
title ZebraOCR OCR Service Toggle
cd /d "%~dp0"

set "PY=C:\Python314\python.exe"
if not exist "%PY%" ( set "PY=python" )
set "SERVER=%~dp0ZebraOCR\ZebraOCR\ocr_server_unlimited.py"
set "MODEL=%~dp0Unlimited-OCR"
set "PORT=5100"

set "NOPAUSE="
if not "%~1"=="" set "NOPAUSE=1"

if /i "%~1"=="start"   call :do_start   & goto end
if /i "%~1"=="stop"    call :do_stop    & goto end
if /i "%~1"=="status"  call :do_status  & goto end
if /i "%~1"=="restart" call :do_restart & goto end
if not "%~1"=="" (
    echo [错误] 未知参数: %~1 （可用: start / stop / status / restart）
    goto end
)

:menu
echo.
echo  ==================================================
echo    ZebraOCR OCR 服务启停工具  (Unlimited-OCR)
echo    端口: %PORT%    模型: %MODEL%
echo  ==================================================
echo    [1] 启动 OCR 服务
echo    [2] 停止 OCR 服务
echo    [3] 查看状态 (端口 / GPU 显存)
echo    [4] 重启服务
echo    [0] 退出
echo  ==================================================
set "ACT="
set /p "ACT=请选择 (0-4): "
if "%ACT%"=="1" call :do_start
if "%ACT%"=="2" call :do_stop
if "%ACT%"=="3" call :do_status
if "%ACT%"=="4" call :do_restart
if "%ACT%"=="0" exit /b 0
goto menu

:do_start
powershell -NoProfile -ExecutionPolicy Bypass -Command "if (Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"
if errorlevel 1 (
    echo [提示] 端口 %PORT% 已被占用，OCR 服务可能已在运行。
    echo        请先执行 [2] 停止，或运行 [3] 查看状态。
    goto :eof
)
echo [启动] 正在独立窗口启动 OCR 服务 (端口 %PORT%)，请在新窗口观察模型加载日志...
start "ZebraOCR OCR 服务 (端口 %PORT%)" /d "%~dp0" "%PY%" "%SERVER%" --port %PORT% --model "%MODEL%"
echo [完成] 服务窗口已打开，模型加载需 1~3 分钟。
goto :eof

:do_stop
echo [停止] 正在结束 OCR 服务进程...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-CimInstance Win32_Process -Filter \"Name='python.exe'\" | Where-Object { $_.CommandLine -like '*ocr_server_unlimited.py*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; Write-Host ('已停止 OCR 服务 PID ' + $_.ProcessId) }; $c = Get-NetTCPConnection -LocalPort %PORT% -State Listen -ErrorAction SilentlyContinue; if ($c) { $c | ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue; Write-Host ('已停止端口占用 PID ' + $_.OwningProcess) } } else { Write-Host ('端口 %PORT% 已无监听，服务已停止') }"
echo [完成] 停止操作结束。
goto :eof

:do_status
echo [状态] 端口 %PORT% 监听情况:
netstat -ano | findstr ":%PORT%" | findstr "LISTENING"
echo.
echo [状态] GPU 显存 / 利用率:
nvidia-smi --query-gpu=memory.used,memory.total,utilization.gpu --format=csv
goto :eof

:do_restart
call :do_stop
timeout /t 2 /nobreak >nul
call :do_start
goto :eof

:end
echo.
if defined NOPAUSE exit /b 0
pause
goto menu
