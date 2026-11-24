# winget packaging

Kapeyn is published to the [Windows Package Manager](https://learn.microsoft.com/windows/package-manager/) as **`Kapeyn.Kapeyn`**:

```powershell
winget install Kapeyn.Kapeyn
```

winget installs the release's `Kapeyn-Windows.zip` as a *portable* app. It extracts the zip to `%LOCALAPPDATA%\Microsoft\WinGet\Packages\Kapeyn.Kapeyn_…` and adds the `kapeyn` command, which points at `kapeyn-cli.exe` (winget doesn't accept `.com` files). `kapeyn on` then starts the tray app. The command follows winget's link back to the real folder to find `Kapeyn.exe`.

## Publishing a new version

1. Publish the GitHub release first (push a `v*` tag). It must include `SHA256SUMS.txt`.
2. Generate and validate the manifests:

   ```powershell
   powershell -ExecutionPolicy Bypass -File packaging\winget\new-manifest.ps1 -Version 2.3.1
   winget validate --manifest packaging\winget\manifests\k\Kapeyn\Kapeyn\2.3.1
   ```

3. Copy the version folder into a fork of [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs) at `manifests/k/Kapeyn/Kapeyn/<version>/` and open a pull request. Microsoft's bots validate and test it, then a moderator merges it. That usually takes a few days.

   After the first version is accepted, `wingetcreate update Kapeyn.Kapeyn --version <version> --urls <zip url> --submit` can do step 3 in one go.
