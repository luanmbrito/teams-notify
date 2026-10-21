# Gera publicar\TeamsNotify.exe (um único arquivo; precisa do .NET 9 Desktop Runtime na máquina que roda)
# Uso: powershell -ExecutionPolicy Bypass -File publicar.ps1
Set-Location $PSScriptRoot
Get-Process TeamsNotify -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o publicar
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host "Pronto: $PSScriptRoot\publicar\TeamsNotify.exe"
