LUAW {VERSION} - Air traffic control and AI traffic for Microsoft Flight Simulator 2024
=======================================================================================

LUAW is a free, independent add-on that replaces the simulator's ATC with its own
rule-based controllers (Delivery, Ground, Tower, Departure, Center, Approach) and brings
airports to life with AI traffic built from the FSLTL and AIG models you already have.

This is a BETA. Please report bugs and ideas (see "Feedback" below).


REQUIREMENTS
------------
- Windows 10 or 11, 64-bit.
- Microsoft Flight Simulator 2024.
- For AI traffic: FSLTL and/or AIG aircraft models installed. LUAW only uses their
  models and liveries; their own traffic injectors must be turned OFF.
- Optional: a SimBrief account (to load your flight plan).
- Nothing else: .NET is included in the download.


INSTALL
-------
1. Unzip the whole folder anywhere (for example Documents\LUAW).
2. Open the "LUAW" folder and run LUAW.exe.
3. Windows may show "Windows protected your PC" because the app is not code-signed.
   Click "More info" and then "Run anyway".

LUAW does not modify the simulator or its Community folder.


SIMULATOR SETUP (once)
----------------------
- Turn off the simulator's own AI air traffic (airliners and general aviation), so it
  does not compete with LUAW's traffic.
- Turn off the FSLTL injector and any other traffic injector (for example AIG Traffic
  Controller).
- Set the simulator's ATC volume to 0, and do not let the simulator's copilot handle
  communications.
- Do not load an IFR flight plan in the simulator: the plan lives in LUAW.


FIRST FLIGHT
------------
1. Start the simulator and load your flight at a gate or parking spot.
2. Start LUAW. It connects by itself and loads the airport where you are.
3. Flight plan -> enter your SimBrief Pilot ID or username -> Load from SimBrief.
   (You can also enter a plan manually.)
4. Press "Start traffic" when you are ready.
5. Talk to ATC from the Communications panel or the radar card "What to say now":
   - Text: TAB, arrow keys and Enter.
   - Voice: push-to-talk with local speech recognition (download it in Settings).
   - Copilot: reads back and tunes for you.
6. The "ATC manual" page explains every phase with the phrases to use.

You can close LUAW and open it again with the simulator running: it removes its
aircraft when it closes and offers to resume your flight.


OPTIONAL DOWNLOADS (Settings)
-----------------------------
- ATC voices (Piper, about 500 MB) and the Kokoro voice.
- Speech recognition for the microphone (Whisper, about 150 MB).
Everything runs on your CPU, without internet once installed.


YOUR DATA
---------
Settings, logs, flights and caches are stored in %LOCALAPPDATA%\LUAW.
Logs are in %LOCALAPPDATA%\LUAW\logs (Diagnostics -> Open data folder).


UNINSTALL
---------
Delete the LUAW folder you unzipped, and optionally %LOCALAPPDATA%\LUAW.


FEEDBACK
--------
Bugs, ideas and comments (anonymous form, no sign-in): https://forms.gle/voKhCbMmKeNZTsLP9
Also from the app: Settings -> About LUAW -> Send feedback.
For a bug, press Diagnostics -> Export logs (zip): it saves a zip with your logs on the
Desktop. Upload it to Google Drive, Dropbox or similar and paste the link in the form.
Updates are published on flightsim.to (search "LUAW"). When a new version is out, LUAW tells
you when it starts (it checks the latest version on GitHub at most once a day; nothing is
downloaded or sent besides that request).
Source code (GPL-3.0): https://github.com/ExOBrienX/LUAW


SUPPORT
-------
LUAW is free and will stay free. If you enjoy it, you can support its development
with an optional donation: https://ko-fi.com/exo_brienx


LICENSE
-------
LUAW is free software under the GNU General Public License v3.0 (LICENSE.txt).
Copyright (c) 2026 ExOBrienX. Third-party components and data are listed in
THIRD-PARTY-NOTICES.txt; their license texts are in the "licenses" folder.

LUAW is not affiliated with or endorsed by Microsoft, Asobo Studio, VATSIM,
Navigraph (SimBrief), FSLTL or AIG. Microsoft Flight Simulator is a trademark of
Microsoft Corporation. Not for real-world aviation use.
