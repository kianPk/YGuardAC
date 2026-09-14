# YGuardAC

Suspicion-score anti-cheat for Counter-Strike 2 (CounterStrikeSharp).

Same idea as [CS2AntiCheat](https://github.com/jaycs1723658/CS2AntiCheat): modules add score → alert → kick → **ban when score ≥ threshold**.

Uses **`pawn.V_angle`** (not broken `EyeAngles`).

## What it detects

| Module | Signal |
|--------|--------|
| RapidFire | Shots faster than weapon cycle |
| Speedhack | Ground speed above limit for many ticks |
| BunnyHop | Long perfect tick-ground hop chains |
| Spinbot | Sustained extreme yaw rate |
| UntrustedAngles | Impossible pitch |
| AimSnap | Huge snap then kill (repeat) |
| Grief | Team damage / team kills |
| SmokeKill | Enemy kills while LOS crosses active smoke |
| Wallbang | Enemy kills with wall penetration (`Penetrated`) |

Soft aim / silent light cheats are **not** reliably caught server-side.

## Defaults (editable in config)

- Alert ≥ **40** (admin chat)
- Kick ≥ **90**
- Ban ≥ **90** with `BanMinKicks: 0` → **bans as soon as score hits 90**
- If you want kick-then-ban like CS2AntiCheat: set `BanThreshold: 120`, `BanMinKicks: 3`

Ban runs:

```text
css_ban #{userid} 0 "YGuardAC auto-ban (score …)"
```

Needs **SimpleAdmin** (or change `BanCommand`). Without it, change command or expect fallback kick only if command fails.

## Build (Linux game node / any machine with .NET 8 SDK)

```bash
sudo apt install -y dotnet-sdk-8.0   # if needed
cd YGuardAC
dotnet build -c Release
```

Output:

```text
bin/Release/net8.0/YGuardAC.dll
```

## Install from GitHub (5stack / yguard panel)

1. Upload this repo to GitHub (public is easiest for the panel zip download).
2. In **Panel → Plugins / Plugin Directory → Custom / Install from URL** use:

```text
https://github.com/<USER>/<REPO>/archive/refs/heads/main.zip
```

3. Typical fields (same style as other CSS plugins):
   - **Layout:** `plugin` (or whatever your panel calls “folder already contains the plugin”)
   - **Path:** `addons/counterstrikesharp/plugins/YGuardAC`
   - Source folder inside the zip: `plugin/YGuardAC` (contains `YGuardAC.dll`)

If the panel only accepts a raw plugin folder zip, zip just `plugin/YGuardAC/` and upload that, or use a GitHub **Release** asset.

### Manual on game node

```bash
mkdir -p /opt/5stack/custom-plugins/addons/counterstrikesharp/plugins/YGuardAC
# copy YGuardAC.dll from plugin/YGuardAC/
```

### Ban command

Needs SimpleAdmin (or change `BanCommand` in config). Config appears after first load under:

```text
addons/counterstrikesharp/configs/plugins/YGuardAC/YGuardAC.json
```


## Commands

| Command | Who | What |
|---------|-----|------|
| `css_ygac` | player | own score |
| `css_ygac_score <userid>` | `@css/ban` | inspect |
| `css_ygac_reset <userid>` | `@css/ban` | reset |

## Safe rollout

1. Set `BanThreshold` to `999` and `KickThreshold` to `999` for a few days (log only + alerts).
2. Watch console `[YGuardAC]` lines for false positives.
3. Lower thresholds when comfortable.

## Not a VAC replacement

This reduces obvious rage cheats and grief. Review demos for soft cheats.
