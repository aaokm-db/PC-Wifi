# Build notes

## Main application

The main executable is built from `PCWifi.cs`. The project uses WPF and Windows Runtime APIs for the Windows Mobile Hotspot integration. `build.ps1` invokes the .NET Framework C# compiler (`csc.exe`) and references the Windows metadata assemblies under `%WINDIR%\System32\WinMetadata`.

Run the script from Windows PowerShell:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\build.ps1
```

The script expects the .NET Framework compiler and WPF reference assemblies at the paths declared near the top of `build.ps1`. If those paths do not exist on your Windows installation, install the matching .NET Framework developer components or update the paths to the installed compiler/reference assemblies.

## Icon

`app.ico` is committed so normal builds do not need to regenerate it. To recreate it:

```powershell
.\GenerateIcon.ps1
```

## Legacy source

`legacy/HotspotKeepAlive.cs` is retained for reference. It is not part of the main executable build and should not be added to the same compilation command because it defines a separate application entry point and overlapping implementation types.
