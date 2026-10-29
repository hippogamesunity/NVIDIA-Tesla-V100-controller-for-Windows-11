# Tesla V100 SXM2 Controller for Windows 11

A Windows console application for Tesla V100 SXM2 cards connected through a PCIe adapter. It installs a shutdown workaround for the recurring **Code 10 after restart** problem and can restore a selected **TCC / WDDM** mode after the NVIDIA driver finishes loading.

## What it does

- Requests administrator access through the executable's UAC manifest.
- Shows GPU status, current mode, saved mode, and installation status in one console.
- Removes the matching GPU **device**, keeping its NVIDIA driver package, at shutdown/restart.
- Runs a background task as **SYSTEM at system boot**, without waiting for user logon.
- Waits up to five minutes for PCI enumeration, NVIDIA driver binding and `nvidia-smi` readiness. A temporary name such as **3D Video Controller** is expected and does not trigger recovery.
- Recovers a persistent Code 10 with device removal followed by one Windows restart. A durable guard prevents an automatic reboot loop.
- Applies a saved WDDM/TCC preference once the GPU is usable, restarting only that GPU and checking its resulting mode.
- Migrates the earlier `V100-AutoRecovery` task and the known desktop script's shutdown entry when **Install Fix** is selected.

The menu stays in English, uses dynamic options numbered from 1, and asks separately before a manual recovery restart. Installing the fix alone does not restart Windows. Close the console window to exit.

## Supported devices

The device filter is deliberately restricted to these PCI IDs:

| Device | PCI device ID |
| --- | --- |
| V100 SXM2 16 GB | `10DE:1DB1` |
| V100 SXM2 32 GB | `10DE:1DB5` |

P100, native PCIe V100, the integrated display GPU, and parent PCIe bridges are excluded. The 16 GB SXM2 setup was used during development; the 32 GB variant needs hardware validation.

## Use

Build or download `TeslaV100Controller.exe`, then launch it and accept UAC.

1. Select **Install Fix** to register the boot task and shutdown policy.
2. Select **Driver mode (TCC / WDDM)** and choose a preference.
3. Apply the mode immediately, or decline the GPU restart to save it for the next boot.

Installation copies the single executable to `%ProgramFiles%\TeslaV100Controller`. Startup and shutdown reference that stable copy, so the original download/build location can change. The installed directories allow changes only by administrators and SYSTEM.

Settings and logs:

```text
%ProgramData%\TeslaV100Controller\settings.json
%ProgramData%\TeslaV100Controller\controller.log
```

The startup timeout is `StartupTimeoutSeconds` in the settings file (default 300; allowed range 30–1800). The log rotates at approximately 2 MB.

**Uninstall** unregisters the boot task, removes owned shutdown entries, and removes saved preferences. It leaves the current GPU mode and driver package in place. The installed executable and diagnostic logs remain; they can be removed after closing the application.

Do not reinstall the old desktop batch workaround alongside this application: both would otherwise act on the GPU at shutdown.

## Startup sequence

1. Find supported PCI hardware IDs, independent of display name or device class.
2. Wait for the NVIDIA service/driver key and a started device.
3. Confirm that `nvidia-smi`, addressed by PCI bus ID, can read the driver model.
4. If NVIDIA is bound but Code 10 remains stable for 15 seconds, remove the device and restart Windows once. If another GPU is still initializing, continue waiting.
5. If the GPU is healthy, clear the recovery guard and restore the saved mode.
6. On a detection/driver timeout or another error code, log the problem and exit without rebooting.

If recovery does not fix Code 10, the next boot logs the failure without another automatic reboot. An explicitly confirmed manual recovery in the menu can retry.

No permanent Windows service is needed for this sequence. The boot task exits after checking/recovering the GPU. The local shutdown policy handles device removal before shutdown; it does not depend on an event-triggered task racing Windows shutdown.

## TCC / WDDM implementation

The mode workaround updates the **detected V100's own driver key**, then calls `pnputil /restart-device` for that device:

| Mode | `AdapterType` | `GridLicensedFeatures` |
| --- | --- | --- |
| WDDM | DWORD `1` | DWORD `7` |
| TCC | DWORD `2` | Value removed (driver default) |

This is a driver-specific workaround, not an official universal mode-switching API. The WDDM method was verified with Data Center **582.99**, where `nvidia-smi -dm 0` returned **Not Supported** despite exit code 0. WDDM was subsequently reported by `nvidia-smi`, and FurMark ran. TCC restores the registry defaults observed on that setup; application-level switching and reboot restoration still require end-to-end hardware testing.

After a mode change, the application checks both PnP state and the actual `nvidia-smi` driver model. It reports a failure if it cannot verify the result. It does not reboot Windows to force a mode change. Close GPU applications before changing modes. Shutdown/recovery closes only LM Studio and its matching local backend processes before removing devices.

This workaround does not fix faulty hardware or make an unsupported card/driver/platform combination officially supported.

Background: [original Windows driver workaround](https://habr.com/ru/posts/1042620/), [Microsoft PnPUtil](https://learn.microsoft.com/windows-hardware/drivers/devtest/pnputil-command-syntax), [Task Scheduler boot triggers](https://learn.microsoft.com/windows/win32/taskschd/starting-an-executable-on-system-boot).

## Command line

Run the published executable from an administrator terminal:

```powershell
.\TeslaV100Controller.exe --status
.\TeslaV100Controller.exe --install
.\TeslaV100Controller.exe --mode wddm
.\TeslaV100Controller.exe --mode tcc
.\TeslaV100Controller.exe --mode keep
.\TeslaV100Controller.exe --uninstall
```

`--mode` saves and immediately applies a preference; it can interrupt GPU workloads. `--mode keep` disables automatic mode restoration. `--startup` and `--shutdown` are integration entry points; `--startup` is allowed to restart Windows for a settled Code 10.

## Build and test

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). Open `TeslaV100Controller.sln` in Visual Studio or VS Code with C# support.

```powershell
dotnet build TeslaV100Controller.sln -c Release
dotnet run --project tests/TeslaV100Controller.Tests -c Release
dotnet publish src/TeslaV100Controller -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o artifacts/win-x64
```

The published EXE includes .NET. Install from this published file, rather than a framework-dependent development build. To debug device-changing code, the debugger must also have administrator access.

The test executable uses simulated GPU operations for recovery and mode restoration. It covers delayed enumeration, transient Code 10, PnP/NVML disagreement, timeouts, reboot guard durability, failed removal, policy preservation and legacy migration. It also validates the task XML through Windows Task Scheduler with `TASK_VALIDATE_ONLY` and performs read-only native device enumeration. It never removes real devices, changes registry values, registers a task or restarts Windows.

The GitHub Actions workflow builds, runs the checks and publishes a downloadable Windows x64 executable artifact. A real reboot/power-loss cycle and mode transitions must be tested separately on the target computer before treating a build as validated for unattended use.
