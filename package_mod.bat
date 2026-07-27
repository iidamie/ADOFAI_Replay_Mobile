@echo off
setlocal
for /f "usebackq delims=" %%v in ("VERSION.txt") do set "VERSION=%%v"

set "OUTPUT=MobilePlugin\bin\Release\net10.0"
set "TMP=tmp_package"

if exist "%TMP%" rmdir /s /q "%TMP%"
mkdir "%TMP%\Replay"
copy /y "%OUTPUT%\Replay.dll" "%TMP%\Replay\Replay.dll" >nul
copy /y "%OUTPUT%\System.Formats.Nrbf.dll" "%TMP%\Replay\System.Formats.Nrbf.dll" >nul

if exist "Replay-%VERSION%.zip" del /q "Replay-%VERSION%.zip"
tar -a -c -f "Replay-%VERSION%.zip" -C "%TMP%" Replay
rmdir /s /q "%TMP%"
echo Replay-%VERSION%.zip
pause
