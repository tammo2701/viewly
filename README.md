# Viewly

Portable Windows film and series player, powered by VideoLAN LibVLC. Viewly keeps its media library, playback positions, metadata, and artwork beside the application on the USB drive.

## First run

1. Extract the complete portable package onto a USB drive. Keep the support files and `libvlc` folder beside `Viewly.exe`.
2. Put video files in the `Media` folder next to `Viewly.exe` (Viewly creates it on first launch).
3. Start `Viewly.exe`. Viewly currently runs only when Windows reports its drive as a removable drive.

`ViewlyData` stores local app data on the same drive. Viewly scans only `Media`, does not follow reparse points, disables VLC configuration and plug-in cache writes, and refuses to start its library on a fixed drive. USB devices that Windows reports as fixed disks are not supported yet.

## Build

Requires the .NET 10 SDK and Windows Desktop workload/reference packs.

```powershell
dotnet publish .\src\Viewly\Viewly.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o .\publish
```

The VLC native engine and plugins are included from the VideoLAN NuGet package. Copy the complete publish folder to the USB drive; `Viewly.exe` uses adjacent runtime files and must not be separated from them. The GitHub Actions workflow creates a ZIP package.

## Metadata

Automatic lookup uses TMDB. Enter your own TMDB API Read Access Token under Settings; Viewly stores it in plain text in `ViewlyData/settings.json` on the USB drive so it travels with the library. Do not share that file. The token is not included in source control or the public release. Initial lookups require internet access; downloaded artwork and metadata are cached on the USB drive. Viewly sends the media filename/title to TMDB to perform searches.

This product uses the TMDB API but is not endorsed or certified by TMDB. TMDB data and images are used under their non-commercial terms; see [TMDB's FAQ](https://developer.themoviedb.org/docs/faq).

## Licensing

Viewly uses LibVLCSharp and VideoLAN LibVLC. Review and include the licenses/notices distributed with those dependencies in releases. LibVLC is LGPL-2.1-or-later; LibVLCSharp is LGPL-2.1-or-later. The application's license is in `LICENSE`.
