# Tera Finder official-style release packaging

Our releases follow the packaging structure used by upstream `Manu098vm/Tera-Finder` release `4.3.8`:

| Archive | **Only** archive member |
| --- | --- |
| `Win_x64_TeraFinder.Launcher_vX.Y.Z.zip` | `TeraFinder.Launcher.exe` |
| `Win_x86_TeraFinder.Launcher_vX.Y.Z.zip` | `TeraFinder.Launcher.exe` |
| `TeraFinder.Plugins_vX.Y.Z.zip` | `TeraFinder.Plugins.dll` |

Verified upstream v4.3.8 zip structures with `gh release download 4.3.8 --repo Manu098vm/Tera-Finder` and `unzip -Z1`: all three archives contain exactly **one** file. The old fork v4.3.9 assets instead contained 22 Launcher entries and four plugin ZIP entries, including build directories, runtimeconfig and deps files.

## Release steps

1. Make sure code and tests are complete on `main`.
2. Open GitHub Actions → **Official-style single-file Release** → **Run workflow**, and supply the intended version tag (example: `v4.3.9`). Version tags also trigger release building automatically.
3. The workflow builds the pinned package-compatible PKHeX.Core using the same dependency-replacement process as our regular CI, runs the full test suite, then publishes framework-dependent `win-x64` and `win-x86` **single-file launchers**. It copies the already woven `TeraFinder.Plugins.dll` into a separate plugin archive.
4. Each ZIP is tested to have exactly one expected file. If a build or a test fails, it cannot upload to the public GitHub Release.
5. The Release receives three ZIPs only; SHA-256 hashes go into the release description rather than a separate fourth asset.

The launcher is framework-dependent (requires Windows .NET 10 Desktop Runtime). This makes it compact rather than shipping hundreds of runtime dependencies. Neither the launcher nor plugin archive should include `bin/`, `Release/`, `.deps.json`, `.runtimeconfig.json`, or support DLL files as loose ZIP entries.

**Important:** never zip `TeraFinder.Launcher/bin/` or `TeraFinder.Plugins/bin/` directly for release publication. Those folders are developer/CI build outputs, *not* distributable end-user packages.

## Validation

Windows CI checks binary generation and ZIP membership. A Windows user should still open the single-file Launcher on x64 and x86 machines, load an SV save and test a batch export. PKHeX plugin behavior also requires manual verification with a compatible PKHeX build.

**Chinese note:** Release 压缩包采用原版的“两个架构各一个 EXE、插件一个 DLL”结构；Release 不直接分发完整的 bin 构建文件夹。
