🌙 **Better Night** — initial release

A darker, more realistic night for **Nuclear Option**, with procedural street lighting. 100% client-side & multiplayer-safe, and it fully reverts when you toggle it off.

**What it does:**
• 🌑 Lifts the pitch-black night to a plausible moonlit floor (world only — your cockpit stays dark)
• 💡 Makes existing lights read: city windows, vehicle/ship/aircraft nav & engine lights, beacons/antennas — plus a draw-distance lever so distant city glow doesn't vanish
• 🛣️ Lines the whole road network with **cobra-head street lamps**, auto-classified City / Main / Rural (white LED vs warm sodium)
• 🔦 Real **ground-light pools** via a custom render pass — no 8-light-per-surface cap, so the entire map lights up
• 💥 Lamp poles are **physically knocked down** by explosion shockwaves (real overpressure — violent up close, standing at the edge)
• 🎛️ Everything tunable live via **F1** (ConfigurationManager)

**Requires:** BepInEx 5 + ConfigurationManager. Install via NOMM, or drop the two files into `BepInEx/plugins/RealisticNight/`.

_Note: the plugin file is `RealisticNight.dll` (the project's original name) — the mod is Better Night._
