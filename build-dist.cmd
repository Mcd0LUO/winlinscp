@echo off
rem 一键生成发布版到 dist\（优先 --no-restore 免联网；资产缺失时回退完整发布）
cd /d "%~dp0"
dotnet publish src\WinLinScp\WinLinScp.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false --no-restore -o dist
if errorlevel 1 (
  echo.
  echo [提示] 缺少 restore 资产，执行完整发布（需联网，可能较慢）...
  dotnet publish src\WinLinScp\WinLinScp.csproj -c Release -r win-x64 --self-contained false -p:DebugType=None -p:DebugSymbols=false --ignore-failed-sources -o dist
)
if errorlevel 1 (
  echo.
  echo [失败] 生成失败，请检查上方错误。
  pause
  exit /b 1
)
rmdir /s /q src\WinLinScp\bin 2>nul
echo.
echo [完成] 已生成 dist\（免联网，约 1-2 秒）
pause
