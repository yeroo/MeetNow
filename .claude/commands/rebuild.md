Rebuild and restart the MeetNow app.

Steps:
1. Check if MeetNow.exe is currently running using `tasklist /FI "IMAGENAME eq MeetNow.exe"` via Bash
2. If running, kill it with `taskkill /IM MeetNow.exe /F`
3. Run `dotnet build MeetNow/MeetNow.csproj` and show the result
4. If the app WAS running in step 1, restart it by launching `MeetNow/bin/Debug/net8.0-windows10.0.19041.0/win-x64/MeetNow.exe` in the background using Bash with `run_in_background: true`
5. Report whether build succeeded and whether the app was restarted
