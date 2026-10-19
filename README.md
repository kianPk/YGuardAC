# YGuardAC

Suspicion-score anti-cheat for Counter-Strike 2 (CounterStrikeSharp).

Same idea as [CS2AntiCheat](https://github.com/jaycs1723658/CS2AntiCheat): modules add score → alert → kick → **ban when score ≥ threshold**.

Uses **`pawn.V_angle`** (not broken `EyeAngles`).

Several hard edges are inspired by [OSAntiCheat](https://github.com/Pintuzoft/OSAntiCheat) concepts (no code copied): silent aim, precision snap, blind HS burst, name churn.

## What it detects

| Module | Signal | Edge |
|--------|--------|------|
| RapidFire | Shots faster than weapon cycle | score |
| Speedhack | Ground speed above limit for many ticks | score |
| BunnyHop | Long perfect tick-ground hop chains | score |
| Spinbot | Sustained extreme yaw rate (off by default) | score |
| UntrustedAngles | Impossible pitch | score |
| AimSnap | Large snap then kill **or** approach≥5° → land≤0.5° | score + kick after N |
| Grief | Team damage / team kills | score |
| SmokeKill | Enemy kills while LOS crosses active smoke | kick after N |
| Wallbang | Enemy kills with wall penetration | kick after N |
| SilentAim | Burst-opener damage while view ≥10° off victim (lag-comp) | score + kick after N |
| SilentAimFar | Far precision headshots ≥4° off (distinct victims) | kick at 2 |
| BlindHsBurst | ≥4 distinct blind headshot kills in 15s | kick |
| NameChurn | ≥3 renames in 20s | kick |

Soft aim / wallhack without geometry are **not** fully covered server-side.

## Defaults (editable in config)

- Alert ≥ **40** (admin chat)
- Kick ≥ **90**
- Ban ≥ **90** with `BanMinKicks: 0` → **bans as soon as score hits 90**
- SilentAim kick after **3** off-view burst openers
- AimSnap kick after **4** precision snaps
- BlindHsBurst kick at **4** distinct blind HS in 15s
- NameChurn kick at **3** renames / 20s

Ban runs:

```text
css_ban #{userid} 0 "YGuardAC auto-ban (score …)"
```

Needs **SimpleAdmin** (or change `BanCommand`). Without it, change command or expect fallback kick only if command fails.

## Build

```bash
cd YGuardAC
dotnet build -c Release
```

Output:

```text
bin/Release/net10.0/YGuardAC.dll
```

## Install

Copy `YGuardAC.dll` to:

```text
addons/counterstrikesharp/plugins/YGuardAC/
```

Config appears after first load under:

```text
addons/counterstrikesharp/configs/plugins/YGuardAC/YGuardAC.json
```

## Commands

| Command | Who | What |
|---------|-----|------|
| `css_ygac` | player | own score |
| `css_ygac_score <userid>` | `@css/ban` | inspect |
| `css_ygac_reset <userid>` | `@css/ban` | reset |
| `css_ygac_debug` | player | session counters |

## Safe rollout

1. Raise kick counters (`KickAfterHits`, `BanAfterMatchHits`, `KickAtDistinct`) for a few days if needed.
2. Watch console `[YGuardAC]` / `SILENT-KICK` / `BLIND-HS-KICK` lines.
3. Lower thresholds when comfortable.

## Not a VAC replacement

This reduces obvious rage cheats and grief. Review demos for soft cheats. Pair with the Windows YGuardAC launcher for client-side integrity.
