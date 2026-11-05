# CalibrationForge

An experimental C#/.NET 8 offline calibration workbench. There is no live adapter driver, physical ECM connection, kernel upload/recovery, or flashing.

## Run locally

Windows and the .NET SDK pinned in `global.json` are required for the WPF desktop.

```powershell
dotnet run --project src\CalibrationForge.Desktop
dotnet run --project src\CalibrationForge.Cli -- help
```

Close the desktop before rebuilding its normal output directory. To build and verify without replacing files used by an open application:

```powershell
.\scripts\Verify.ps1
```

The script uses separate output under `artifacts\verification`, builds Release, runs unit tests, and runs an off-screen WPF smoke-check executable. It never closes another application. GitHub Actions uses the same script on Windows when this repository is hosted on GitHub.

If an apphost cannot locate the installed runtime despite a working SDK, check `DOTNET_ROOT`; it must point to the intended .NET installation.

## What is supported

- Inspect BIN size/hash; select experimental dwell and stoichiometric air/fuel-ratio tables in an exact-hash public E92 sample. Additional-table editing is off by default and requires explicit opt-in. Other images remain read-only.
- Analyze simulated/imported offline diagnostic responses; missing responses remain unknown.
- Replay standard OBD identification and an experimental already-running RAM-reader protocol using explicitly labeled fixtures.
- Compare two fresh file reads and export an atomic ZIP containing image bytes, hash, and provenance. Consistent file reads do not validate an ECM.
- Analyze five observed additive calibration checks in one exact public reference. This is not complete checksum/CVN validation or checksum repair.

Edited BIN exports preserve the loaded original and are **not flash-ready**. Definitions and sample provenance are not OEM/hardware validated. The simulator is a separate 16-byte demonstration, not an ECM.

## Experimental E92 definitions

Only the exact original SHA256 in `ExperimentalBinProfile` is recognized; OS number alone is insufficient. Editing, undo/redo, revert-all and export review cover both tables. Disabling additional-table editing prevents new cell edits there, but preserves existing edits, history and export review.

| Definition | Data offset | Layout | Decode |
|---|---|---|---|
| Base ignition dwell | `0xAC7F0` | 33 voltage rows × 17 RPM columns; preceding `00110021` header | BE unsigned 16-bit / 128 ms |
| Stoichiometric air/fuel ratio | `0x842CC` | 17 fuel-alcohol rows × 1 column; preceding `0011` count | BE unsigned 16-bit / 2048 AFR |

Community metadata comes from [UniversalPatcher TableSeek-e92.xml, commit de421df](https://github.com/joukoy/UniversalPatcher/blob/de421df36e195c3afc45e589aea0210afdf6beff/XML/TableSeek-e92.xml). In the exact public sample, the stoichiometry signature occurs at `0x25F98F`, with address-load bytes `7060E0081C6342CA` at `0x25F996`: address `0xE00842CA`, using the `0xE0000000` mapping also evidenced by the dwell references. The count at file offset `0x842CA` is 17; the following 17 words are `0x70DE`, decoding to 14.1083984375 AFR. The upstream stoichiometry entry's E80-base adjustment (`-0xE0800000 + 2`) does **not** apply to this sample. There is no generic address-base correction or signature-based editing of unknown images.

The fuel-alcohol axis follows the community metadata (0 to approximately 99.9985% in 17 points), not an independently validated OEM breakpoint map. Stoichiometry describes fuel composition; it is **not** commanded power enrichment and is not a horsepower adjustment. Spark advance, power enrichment, airflow, cam and torque definitions remain unavailable pending justified locations, formats and axes. No firmware or upstream implementation is included.

## Structure and regression coverage

`CalibrationForge.Core` owns calibration rules, protocol codecs, diagnostic channels, read verification, checksums, and file formats. `CalibrationForge.Cli` and `CalibrationForge.Desktop` are presentation layers.

The communication boundary is `ICanTransport` → `IDiagnosticChannel` → controller-specific clients. `IMemoryReader` separates read coordination from its source. Use existing boundaries rather than introducing an interface for every class.

Unit tests use synthetic calibration layouts and scripted/replayed traffic; they require no public BIN download, adapter, vehicle, or network access during execution. Desktop smoke checks cover actual WPF composition and selected interactions. Neither proves hardware compatibility, smoothness on every display, or complete accessibility.

## Support and publication

Offline workflow support logs retain bounded operation metadata in memory and export only on request. They omit file paths, VINs, controller bytes, and exception messages. Diagnostic reports and dump bundles are different artifacts and can contain source/controller information; review them before sharing.

No open-source license has been selected. Before publication, select a license and review community-derived definitions and fixture attribution.

No firmware images, vehicle dumps, or third-party kernel payloads are bundled. Tests generate synthetic bytes and protocol exchanges; source links and factual sample metadata are not bundled firmware or kernel implementations.

A `.bin` extension does not determine ownership or licensing. Original synthetic test files may be included with their provenance documented; copied firmware does not become synthetic merely by being labeled a fixture. Keep vehicle inputs, captures, and exports in the ignored `local-data` directory or outside the repository. Do not commit personal diagnostic data or support exports.
