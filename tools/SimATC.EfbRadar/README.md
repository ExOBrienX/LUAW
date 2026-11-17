# LUAW Radar (MSFS 2024 EFB app)

Traffic radar for the in-sim tablet (EFB). It is based on the EFB template of the MSFS 2024 SDK
(`Samples\DevmodeProjects\EFB`, MIT license by Asobo Studio). It is not part of the release package yet.

## Build (once, or after changing the code)
```powershell
powershell -ExecutionPolicy Bypass -File tools\SimATC.EfbRadar\setup.ps1
```
It copies the EFB API from your SDK (not included in this repository), installs the dependencies and builds
`PackageSources\RadarApp\dist`.

## Use
1. Start LUAW. Its local server listens on `ws://127.0.0.1:47110/traffic`.
2. In a flight, open the EFB and the **RadarApp**: your aircraft in the center and the traffic with callsign, altitude,
   speed and distance; zoom (2-80 NM), heading-up / north-up and parked aircraft on or off.
3. To install it permanently (without Developer Mode), copy `Packages\simatc-efb-radar` to your Community folder.

## Test without LUAW
`dotnet run --project tools/SimATC.Probe -- --efb-test` sends your aircraft and three test aircraft in the real format.
Do not run LUAW at the same time: they use the same port.

To build it from the simulator instead: enable Developer Mode, open **Tools > Project Editor > Project > Open**, choose
`tools\SimATC.EfbRadar\SimATCRadarProject.xml` and **Build All**. The SDK's Coherent GT Debugger shows the app console.
