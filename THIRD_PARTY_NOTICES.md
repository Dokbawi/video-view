# Third-party components

The desktop application invokes FFmpeg as a separate executable. Package versions are declared in the project files; exact resolved dependencies are recorded by NuGet in `obj/project.assets.json`.

## Public release packaging

The public installer and portable ZIP contain the application and .NET runtime, but **do not redistribute FFmpeg or FFprobe executables**. The setup wizard explains and automatically performs a separate download of the unmodified Gyan package directly to the user's machine. The portable ZIP retains `Install-MediaTools.cmd` as an optional manual setup path. Both verify the archive and executable SHA-256 hashes and retain upstream `LICENSE` and `README.txt` alongside the installed tools. Users may instead configure their own FFmpeg paths.

The local development `scripts/publish.ps1` default can bundle the developer's FFmpeg copy. That private test package is not the public release asset. Use `scripts/package-installer.ps1` for the public installer and ZIP.

The installer is built with unmodified Inno Setup 7.1.0 by Jordan Russell and Martijn Laan (https://jrsoftware.org/). Its license is included at `licenses/InnoSetup.txt`; its original installer notices are retained.

Runtime license texts are preserved in `licenses/`: .NET, NAudio, Vortice.Windows, Vortice.Mathematics, SharpGenTools, and C#/WinRT. The Windows SDK .NET targeting package also references Microsoft's terms at https://aka.ms/WinSDKLicenseURL. VideoView's own code is licensed under GPL-3.0-only; see `LICENSE` and `NOTICE`. Third-party terms are unchanged.

| Component | Version | Upstream / license information |
| --- | --- | --- |
| .NET / WPF / Windows Forms | 10.0 | https://github.com/dotnet/runtime/blob/main/LICENSE.TXT · https://github.com/dotnet/wpf/blob/main/LICENSE.TXT |
| NAudio | 2.2.1 | https://github.com/naudio/NAudio/blob/master/license.txt |
| Vortice.Windows (Direct3D11/DXGI) | 3.8.3 | https://github.com/amerkoleci/Vortice.Windows/blob/main/LICENSE |
| SharpGen.Runtime | NuGet resolved dependency | https://github.com/SharpGenTools/SharpGenTools |
| Windows SDK .NET / C#/WinRT | NuGet resolved dependency | https://github.com/microsoft/CsWinRT |
| FFmpeg essentials build | 9.0.2 | https://www.gyan.dev/ffmpeg/builds/ · bundled `tools/FFmpeg-LICENSE.txt` |

Pinned FFmpeg archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip

SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`

FFmpeg sources for this upstream build: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b. The upstream package's `README.txt` records its configuration and external library versions. Build configuration is also printed by `tools/ffmpeg.exe -version`. The selected essentials build enables GPLv3 components including libx264. The public VideoView ZIP uses the separate upstream download described above.

License source snapshots used for the managed graphics bindings:

- Vortice.Windows 3.8.3: https://github.com/amerkoleci/Vortice.Windows/blob/9e609cb9439c9872aa1b339f177e40ec96f77239/LICENSE
- Vortice.Mathematics 2.1.0: https://github.com/amerkoleci/Vortice.Mathematics/blob/fa05ec6dcba48f3f7331791da6dc7f3d866b2ad6/LICENSE
- SharpGenTools 2.4.2-beta: https://github.com/SharpGenTools/SharpGenTools/blob/6990bcafe124a4c22515ad19cee5a081da8db67b/LICENSE.txt

These components retain their respective copyright and license notices. VideoView does not claim authorship of them.
