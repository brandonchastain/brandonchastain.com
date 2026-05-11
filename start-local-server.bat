@echo off
REM Start a local web server for brandonchastain.com
cd /d %~dp0
python -m http.server 8080
