# Third-party notices

LUAW is free software licensed under the **GNU General Public License v3.0** (see `LICENSE`).
Copyright © 2026 ExOBrienX.

LUAW is an independent project. It is **not affiliated with, endorsed or supported by** Microsoft, Asobo Studio,
VATSIM, Navigraph/SimBrief, FSLTL, AIG or any airline or air navigation service provider. Microsoft Flight Simulator is
a trademark of Microsoft Corporation.

LUAW includes or uses the components and data below. Each keeps its own license. The full license texts shipped with
the bundled libraries are in the `licenses` folder of the download.

## Bundled with the download

### Software libraries

| Component | License | Copyright |
|---|---|---|
| .NET runtime | MIT | © .NET Foundation and Contributors |
| Avalonia UI (Avalonia, Desktop, Fluent theme, Skia) | MIT | © 2013-2026 The AvaloniaUI Project |
| ANGLE for Windows (Avalonia.Angle.Windows.Natives) | BSD-3-Clause | © The ANGLE Project Authors |
| MicroCom.Runtime | MIT | © 2021 Nikita Tsukanov |
| SkiaSharp and HarfBuzzSharp (Skia, HarfBuzz) | MIT (Skia: BSD-3-Clause, HarfBuzz: Old MIT) | © Xamarin, Inc.; © Microsoft Corporation; © Google Inc.; © HarfBuzz contributors |
| Inter font (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | © 2016 The Inter Project Authors |
| CommunityToolkit.Mvvm | MIT | © .NET Foundation and Contributors |
| NAudio (Core, WinMM) | MIT | © Mark Heath |
| Whisper.net and its runtime (whisper.cpp) | MIT | © Sandro Hanea; © Georgi Gerganov |
| ONNX Runtime | MIT | © Microsoft Corporation |
| SimConnect (`SimConnect.dll`, `Microsoft.FlightSimulator.SimConnect.dll`) | Microsoft Flight Simulator SDK license | © Microsoft Corporation |

`SimConnect.dll` and its managed wrapper are distributed unmodified, only so that LUAW can talk to Microsoft Flight
Simulator, as other community add-ons do. All rights belong to Microsoft.

### Data

| Data | Source | License |
|---|---|---|
| `data/vatspy` (FIR names and boundaries) | [VATSpy Data Project](https://github.com/vatsimnetwork/vatspy-data-project) | CC BY-SA 4.0, © VATSpy Data Project contributors (unchanged copy) |
| `data/airlines.json` (airline telephony) | Wikipedia, "List of airline codes"; [OpenFlights](https://openflights.org/data) airline database | CC BY-SA 4.0 (Wikipedia contributors); ODbL 1.0 (OpenFlights). This file is a derived database and is offered under the same terms. |
| `data/routes` (airlines and destinations) | Wikipedia airport articles | CC BY-SA 4.0, © Wikipedia contributors |
| `data/world` (routes by callsign, airports) | [vradarserver/standing-data](https://github.com/vradarserver/standing-data) | CC0 1.0 |
| `data/map/world-50m.json` (coastlines and borders) | [Natural Earth](https://www.naturalearthdata.com/) 1:50m | Public domain |
| `data/atc`, `data/centers.json` (Chilean ATC data) | AIP-CHILE, DGAC Chile | Public aeronautical information; cited in each file |

Phraseology templates follow ICAO Doc 4444, UK CAP 413 and FAA JO 7110.65. Airport, runway, procedure and navigation
data are read at run time from the simulator through SimConnect and are not distributed with LUAW.

## Downloaded or used at run time (not bundled)

These are fetched by LUAW on the user's computer, from their original sources, only when the user enables them.

| Component | License |
|---|---|
| [Piper](https://github.com/rhasspy/piper) text-to-speech | MIT; includes espeak-ng, GPL-3.0, run as a separate process |
| Piper voices: en_US-joe, es_ES-davefx | CC0 |
| Piper voices: en_US-libritts_r, en_GB-vctk | CC BY 4.0 (LibriTTS-R, VCTK) |
| Piper voice: es_ES-sharvard | CC BY 3.0 |
| Piper voice: es_MX-ald | Unlicense |
| Piper voice: es_MX-claude | Apache-2.0 |
| [Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) voice model, [kokoro-onnx](https://github.com/thewh1teagle/kokoro-onnx) files | Apache-2.0; MIT |
| Whisper `ggml-base` speech-recognition model ([whisper.cpp](https://github.com/ggerganov/whisper.cpp)) | MIT |
| [VATSIM](https://vatsim.net) public data feed (online Center frequencies and flight-plan routes, used as a reference) | VATSIM data, used under the VATSIM terms |
| [SimBrief](https://www.simbrief.com) flight plan of the user (user's own account) | Navigraph / SimBrief terms |
| METAR weather reports from [aviationweather.gov](https://aviationweather.gov) (NOAA) | Public domain (U.S. Government work) |

`SimATC.KokoroHost.exe` loads the espeak-ng library downloaded with Piper. Like the rest of LUAW, it is released
under the GPL-3.0, and its source code is in the LUAW repository.
