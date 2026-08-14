@echo off
chcp 65001 >nul
title ZebraOCR OCR Service Quick Start
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start_ocr_service.ps1" %*
set EXITCODE=%ERRORLEVEL%
echo.
echo [ZebraOCR] exit code: %EXITCODE%
pause
exit /b %EXITCODE%
