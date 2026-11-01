# Building FaRajni

## Prerequisites

| Tool | Needed for |
|---|---|
| Windows 10/11 x64 | Everything (WPF + libVLC are Windows-only here) |
| [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) | Building |
| ffmpeg | Optional, only for the editor at runtime |

Any IDE works: Visual Studio 2022, Rider, or VS Code with C# Dev Kit. Open `FaRajni.sln`.

## Debug build & run

```powershell
dotnet build
dotnet run --project src/FaRajni.App
```

The first restore downloads `VideoLAN.LibVLC.Windows` (~40 MB). Later builds are quick.

## Release build

This makes a self-contained build that runs on any Windows x64 PC without .NET installed:

```powershell
dotnet publish src/FaRajni.App -c Release -r win-x64 --self-contained -p:PublishReadyToRun=true -o out
```

Run `out\FaRajni.exe`. You can also run `out\FaRajni.exe --build-vlc-cache` once beforehand. It
pre-builds the libVLC plugin cache, which makes the first launch faster.

## Project layout

```
FaRajni.sln
├── src/FaRajni.Core/       domain + data (net8.0)
├── src/FaRajni.App/        WPF app → FaRajni.exe
└── src/FaRajni.Editor/     WPF editor library (lazy-loaded)
```
