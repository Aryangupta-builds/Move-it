# Run from the MoveIt folder in PowerShell. Saves the full output to build.log so you can paste the errors to Claude.
dotnet build MoveIt.csproj -c Release -p:Platform=x64 2>&1 | Tee-Object -FilePath build.log
Write-Host "`nErrors only:"; Select-String -Path build.log -Pattern ' error ' | ForEach-Object { $_.Line } | Sort-Object -Unique
