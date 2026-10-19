using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace YGuardAC;

public sealed class YGuardACPlugin : BasePlugin, IPluginConfig<YGuardACConfig>
{
    public override string ModuleName => "YGuardAC";
    public override string ModuleVersion => "1.3.0";
    public override string ModuleAuthor => "yguard";
    public override string ModuleDescription => "Suspicion-score anti-cheat with OSAC-style edges + live AC launcher gate";

    public YGuardACConfig Config { get; set; } = new();

    private ScoreManager _scores = null!;
    private float _lastTickTime;
    private float _lastSmokeScan;
    private float _lastSpottedPoll;
    private readonly List<SmokeCloud> _smokes = new();

    /// <summary>When observer last had enemy spotted / damaged this map.</summary>
    private readonly Dictionary<(int Observer, int Enemy), float> _lastSeen = new();

    /// <summary>Rolling blind HS kills: (time, victimSlot, neverSeen).</summary>
    private readonly Dictionary<int, List<(float Time, int VictimSlot, bool NeverSeen)>> _blindKills = new();

    private readonly record struct SmokeCloud(float X, float Y, float Z, float Expiry);

    public void OnConfigParsed(YGuardACConfig config)
    {
        MigrateLegacyConfig(config);
        Config = config;
        _scores = new ScoreManager(config.Score);
        Console.WriteLine(
            $"[YGuardAC] config ready decay={config.Score.DecayPerSecond:F2} banAt={config.Actions.BanThreshold:F0} " +
            $"silent={config.SilentAim.Enabled} blindHs={config.BlindHsBurst.Enabled} nameChurn={config.NameChurn.Enabled}");
    }

    private static void MigrateLegacyConfig(YGuardACConfig c)
    {
        c.ScoreResetMinutes = 4f;

        if (c.Score.DecayPerSecond > 0.25f)
            c.Score.DecayPerSecond = 0.1f;
        if (c.Score.FloorPercentOfPeak > 0f)
            c.Score.FloorPercentOfPeak = 0f;
        if (c.Score.ModuleCooldownSeconds > 0.3f)
            c.Score.ModuleCooldownSeconds = 0.2f;

        if (c.Actions.BanThreshold > 55f)
            c.Actions.BanThreshold = 40f;
        if (c.Actions.KickThreshold > 55f)
            c.Actions.KickThreshold = 40f;
        if (c.Actions.AlertThreshold > 35f)
            c.Actions.AlertThreshold = 20f;

        c.SmokeKill.CooldownSeconds = 0f;
        c.SmokeKill.BanAfterMatchKills = 5;
        c.SmokeKill.KillsThreshold = 5;
        c.SmokeKill.Score = 8f;

        c.Wallbang.CooldownSeconds = 0f;
        c.Wallbang.BanAfterMatchKills = 5;
        c.Wallbang.KillsThreshold = 5;
        c.Wallbang.Score = 8f;

        c.Spinbot.Enabled = false;
        if (c.Spinbot.MinDegPerSecond < 5000f)
            c.Spinbot.MinDegPerSecond = 7200f;
        if (c.Spinbot.ConsecutiveTicks < 40)
            c.Spinbot.ConsecutiveTicks = 48;
        if (c.Spinbot.Score > 10f)
            c.Spinbot.Score = 8f;

        if (c.AimSnap.Occurrences > 6)
            c.AimSnap.Occurrences = 3;
        if (c.AimSnap.BanAfterMatchHits < 1)
            c.AimSnap.BanAfterMatchHits = 4;
        if (c.AimSnap.ApproachMinDeg < 1f)
            c.AimSnap.ApproachMinDeg = 5f;
        if (c.AimSnap.SnapOnTargetMaxDeg <= 0f || c.AimSnap.SnapOnTargetMaxDeg > 2f)
            c.AimSnap.SnapOnTargetMaxDeg = 0.5f;
    }

    public override void Load(bool hotReload)
    {
        _scores ??= new ScoreManager(Config.Score);
        RegisterListener<Listeners.OnTick>(OnTick);

        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull);

        AddCommand("css_ygac", "Show your YGuardAC score", OnSelfScore);
        AddCommand("css_ygac_score", "Inspect a player score by userid", OnInspectScore);
        AddCommand("css_ygac_reset", "Reset a player score by userid", OnResetScore);
        AddCommand("css_ygac_debug", "Debug smoke/wallbang/silent counters", OnDebug);

        Console.WriteLine("[YGuardAC] Loaded v1.3.0 — SilentAim + SnapV2 + BlindHs + NameChurn edges.");

        _ = Task.Run(async () =>
        {
            for (int i = 0; i < 3; i++)
            {
                await MatchAbort.RefreshMatchIdCacheAsync();
                await Task.Delay(5000);
            }
        });
        AddTimer(30f, () => { _ = MatchAbort.RefreshMatchIdCacheAsync(); },
            CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);

        AddTimer(6f, () =>
        {
            if (!Config.Enabled) return;
            var targets = new List<(ulong steam, int userId)>();
            foreach (var p in Utilities.GetPlayers())
            {
                if (p is null || !p.IsValid || p.IsBot || p.IsHLTV) continue;
                if (p.Connected != PlayerConnectedState.Connected) continue;
                if (p.UserId is not int uid) continue;
                targets.Add((p.SteamID, uid));
            }
            if (targets.Count == 0) return;
            _ = Task.Run(async () =>
            {
                foreach (var (steam, userId) in targets)
                {
                    try
                    {
                        var (allowed, reason) = await AcGate.CheckAsync(steam);
                        if (allowed) continue;
                        Server.NextFrame(() =>
                        {
                            var p = Utilities.GetPlayers()
                                .FirstOrDefault(x => x is not null && x.IsValid && x.UserId == userId);
                            if (p is null) return;
                            AcGate.KickIfDenied(p, reason);
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[YGuardAC] AC gate sweep error: {ex.Message}");
                    }
                }
            });
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnTick>(OnTick);
        _scores.Clear();
        _smokes.Clear();
        _lastSeen.Clear();
        _blindKills.Clear();
    }

    private void OnTick()
    {
        if (!Config.Enabled) return;

        float now = Server.CurrentTime;
        float dt = Math.Max(1f / 128f, now - _lastTickTime);
        if (_lastTickTime <= 0f) dt = 1f / 64f;
        _lastTickTime = now;

        if (_smokes.Count > 0)
            _smokes.RemoveAll(s => now > s.Expiry);

        if (now - _lastSmokeScan >= 0.5f)
        {
            _lastSmokeScan = now;
            ScanSmokeEntities(now);
        }

        bool pollSight = Config.BlindHsBurst.Enabled &&
                         now - _lastSpottedPoll >= Math.Max(0.05f, Config.BlindHsBurst.PollSeconds);
        if (pollSight)
            _lastSpottedPoll = now;

        var humans = new List<CCSPlayerController>();
        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidHuman(player)) continue;
            humans.Add(player!);
        }

        if (pollSight)
            PollSightMemory(humans, now);

        foreach (var player in humans)
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) continue;

            var st = _scores.GetOrCreate(player.Slot, player.SteamID, player.PlayerName ?? "player", now);
            if (st.ScoreWindowStart < 0f)
                st.ScoreWindowStart = now;

            // Position history for silent-aim lag-comp.
            var origin = pawn.AbsOrigin;
            if (origin is not null)
                st.Positions.Push(now, origin.X, origin.Y, origin.Z);

            // Name churn
            if (Config.NameChurn.Enabled && !IsExemptFromDetection(player))
                CheckNameChurn(player, st, now);

            float resetSeconds = Math.Max(60f, Config.ScoreResetMinutes * 60f);
            if (now - st.ScoreWindowStart >= resetSeconds)
            {
                _scores.ResetScoreWindow(st, now);
                if (Config.VerboseConsole)
                    Console.WriteLine($"[YGuardAC] score window reset for {st.Name} after {Config.ScoreResetMinutes:F0}m");
                player.PrintToChat($" \x04[YGuardAC]\x01 Score reset ({Config.ScoreResetMinutes:F0} min window).");
            }

            if (IsExemptFromDetection(player)) continue;
            CheckAnglesAndMovement(player, pawn, st, now, dt);
            ProcessActions(player, st, now);
        }
    }

    private void PollSightMemory(List<CCSPlayerController> humans, float now)
    {
        foreach (var observer in humans)
        {
            var obsPawn = observer.PlayerPawn.Value;
            if (obsPawn is null || !obsPawn.IsValid) continue;

            foreach (var enemy in humans)
            {
                if (enemy.Slot == observer.Slot) continue;
                if (enemy.TeamNum == observer.TeamNum) continue;

                var enemyPawn = enemy.PlayerPawn.Value;
                if (enemyPawn is null || !enemyPawn.IsValid) continue;

                bool spotted = SpottedUtil.IsSpottedBySlot(enemyPawn, observer.Slot);
                if (!spotted)
                {
                    // Fallback: damage history already fills _lastSeen; also treat very close
                    // facing enemies as "seen" to cut pistol-round false blinds.
                    var o = obsPawn.AbsOrigin;
                    var e = enemyPawn.AbsOrigin;
                    if (o is not null && e is not null)
                    {
                        float dist = AngleUtil.Distance3D(o.X, o.Y, o.Z, e.X, e.Y, e.Z);
                        if (dist < 400f &&
                            AngleUtil.TryGetViewAngles(obsPawn, out float pitch, out float yaw) &&
                            AngleUtil.ViewErrorToBody(o.X, o.Y, o.Z + AngleUtil.EyeHeight, pitch, yaw, e.X, e.Y, e.Z) < 8f)
                        {
                            spotted = true;
                        }
                    }
                }

                if (spotted)
                    _lastSeen[(observer.Slot, enemy.Slot)] = now;
            }
        }
    }

    private void CheckNameChurn(CCSPlayerController player, PlayerAcState st, float now)
    {
        string current = player.PlayerName ?? "";
        if (string.IsNullOrEmpty(st.TrackedName))
        {
            st.TrackedName = current;
            return;
        }

        if (string.Equals(st.TrackedName, current, StringComparison.Ordinal))
            return;

        st.TrackedName = current;
        st.NameChangeTimes.Add(now);
        float window = Config.NameChurn.WindowSeconds;
        st.NameChangeTimes.RemoveAll(t => now - t > window);

        if (st.NameChangeTimes.Count < Config.NameChurn.MinChanges)
            return;

        AddScore(player, st, "NameChurn", Config.NameChurn.Score, now,
            $"{st.NameChangeTimes.Count} renames/{window:F0}s", 5f);

        if (Config.NameChurn.Kick && !IsExemptFromPunishment(player))
        {
            Console.WriteLine($"[YGuardAC] NAME-CHURN-KICK {st.Name} [{st.SteamId}] x{st.NameChangeTimes.Count}");
            NotifyAdmins($"NAME-CHURN-KICK {st.Name} steam={st.SteamId} x{st.NameChangeTimes.Count}");
            KickPlayer(player, "YGuardAC: CHEAT DETECTED: name churn");
            AbortAfterCheat($"{st.Name} name-churn");
            st.NameChangeTimes.Clear();
            _scores.Remove(player.Slot);
        }
    }

    private void CheckAnglesAndMovement(CCSPlayerController player, CCSPlayerPawn pawn, PlayerAcState st, float now, float dt)
    {
        bool onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
        if (Config.BunnyHop.Enabled)
        {
            if (onGround)
            {
                st.GroundTicks++;
            }
            else if (st.OnGround && !onGround)
            {
                if (st.GroundTicks > 0 && st.GroundTicks <= Config.BunnyHop.MaxGroundTicks)
                {
                    st.PerfectBhopChain++;
                    if (st.PerfectBhopChain >= Config.BunnyHop.PerfectChain)
                    {
                        AddScore(player, st, "BunnyHop", Config.BunnyHop.Score, now,
                            $"perfect chain x{st.PerfectBhopChain}");
                        st.PerfectBhopChain = 0;
                    }
                }
                else
                {
                    st.PerfectBhopChain = 0;
                }

                st.GroundTicks = 0;
            }

            if (onGround && st.GroundTicks > Config.BunnyHop.MaxGroundTicks + 2)
                st.PerfectBhopChain = 0;
        }

        st.OnGround = onGround;

        if (Config.Speedhack.Enabled)
        {
            var vel = pawn.AbsVelocity;
            float speed2d = MathF.Sqrt(vel.X * vel.X + vel.Y * vel.Y);
            if (onGround && speed2d > Config.Speedhack.MaxSpeed)
            {
                st.SpeedOverTicks++;
                if (st.SpeedOverTicks >= Config.Speedhack.ConsecutiveTicks)
                {
                    AddScore(player, st, "Speedhack", Config.Speedhack.Score, now,
                        $"{speed2d:F0} u/s");
                    st.SpeedOverTicks = 0;
                }
            }
            else
            {
                st.SpeedOverTicks = 0;
            }
        }

        if (!AngleUtil.TryGetViewAngles(pawn, out float pitch, out float yaw))
            return;

        if (Config.UntrustedAngles.Enabled && MathF.Abs(pitch) > Config.UntrustedAngles.MaxPitch)
        {
            AddScore(player, st, "UntrustedAngles", Config.UntrustedAngles.Score, now,
                $"pitch={pitch:F1}");
        }

        if (st.HasAngles)
        {
            st.PrevPitch = st.Pitch;
            st.PrevYaw = st.Yaw;
            st.HasPrevAngles = true;

            float yawRate = MathF.Abs(AngleUtil.YawDelta(st.Yaw, yaw)) / Math.Max(dt, 0.001f);
            float snap = AngleUtil.AngleDelta(st.Pitch, st.Yaw, pitch, yaw);

            if (Config.Spinbot.Enabled)
            {
                if (yawRate >= Config.Spinbot.MinDegPerSecond)
                {
                    st.SpinTicks++;
                    if (st.SpinTicks >= Config.Spinbot.ConsecutiveTicks)
                    {
                        AddScore(player, st, "Spinbot", Config.Spinbot.Score, now,
                            $"{yawRate:F0} deg/s");
                        st.SpinTicks = 0;
                    }
                }
                else
                {
                    st.SpinTicks = 0;
                }
            }

            if (Config.AimSnap.Enabled && snap >= Config.AimSnap.MinSnapDegrees)
            {
                st.LastSnapTime = now;
                st.LastSnapDegrees = snap;
            }
        }

        st.Pitch = pitch;
        st.Yaw = yaw;
        st.HasAngles = true;
    }

    public HookResult OnWeaponFire(EventWeaponFire @event, GameEventInfo info)
    {
        if (!Config.Enabled) return HookResult.Continue;

        var player = @event.Userid;
        if (!IsValidHuman(player) || IsExemptFromDetection(player!)) return HookResult.Continue;

        float now = Server.CurrentTime;
        var st = _scores.GetOrCreate(player!.Slot, player.SteamID, player.PlayerName ?? "player", now);
        string weapon = @event.Weapon ?? "";

        float gapSinceFire = st.LastFireTime > 0f ? now - st.LastFireTime : 999f;
        st.LastFireWasBurstOpener =
            gapSinceFire >= (Config.SilentAim.Enabled ? Config.SilentAim.BurstWindowSeconds : 0.25f);

        if (Config.RapidFire.Enabled &&
            WeaponCycle.TryGetCycle(weapon, out float cycle) &&
            st.LastWeapon == weapon &&
            st.LastFireTime > 0f)
        {
            float gap = gapSinceFire;
            float minAllowed = cycle * Config.RapidFire.ToleranceMultiplier;
            if (gap > 0f && gap < minAllowed)
            {
                st.RapidFireHits++;
                if (st.RapidFireHits >= Config.RapidFire.Threshold)
                {
                    AddScore(player, st, "RapidFire", Config.RapidFire.Score, now,
                        $"{weapon} {gap * 1000:F0}ms < {minAllowed * 1000:F0}ms");
                    st.RapidFireHits = 0;
                }
            }
            else if (gap >= minAllowed)
            {
                st.RapidFireHits = Math.Max(0, st.RapidFireHits - 1);
            }
        }

        st.LastWeapon = weapon;
        st.LastFireTime = now;
        return HookResult.Continue;
    }

    public HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        if (!Config.Enabled) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (!IsValidHuman(attacker) || victim is null || !victim.IsValid) return HookResult.Continue;

        float now = Server.CurrentTime;
        // Sight memory for BlindHs comes from the spotted poll only — not from damage
        // (every kill has a hurt, which would erase "blind").

        var atk = attacker!;
        if (IsExemptFromDetection(atk)) return HookResult.Continue;

        // Team damage (grief)
        if (Config.Grief.Enabled &&
            atk.TeamNum == victim.TeamNum &&
            atk.Slot != victim.Slot)
        {
            var stGrief = _scores.GetOrCreate(atk.Slot, atk.SteamID, atk.PlayerName ?? "player", now);
            stGrief.TeamDamage += @event.DmgHealth;
            if (stGrief.TeamDamage >= Config.Grief.TeamDamageHp)
            {
                AddScore(atk, stGrief, "TeamDamage", Config.Grief.TeamDamageScore, now,
                    $"{stGrief.TeamDamage:F0} HP");
                stGrief.TeamDamage = 0;
            }
        }

        bool enemy = atk.TeamNum != victim.TeamNum && atk.Slot != victim.Slot;
        if (enemy && Config.SilentAim.Enabled)
            CheckSilentAim(atk, victim, @event, now);

        return HookResult.Continue;
    }

    private void CheckSilentAim(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        EventPlayerHurt @event,
        float now)
    {
        string weapon = @event.Weapon ?? "";
        if (WeaponCycle.IsExcludedFromSilentAim(weapon)) return;

        var aPawn = attacker.PlayerPawn.Value;
        var vPawn = victim.PlayerPawn.Value;
        if (aPawn is null || !aPawn.IsValid || vPawn is null || !vPawn.IsValid) return;
        if (!AngleUtil.TryGetViewAngles(aPawn, out float pitch, out float yaw)) return;

        var aOrigin = aPawn.AbsOrigin;
        var vOrigin = vPawn.AbsOrigin;
        if (aOrigin is null || vOrigin is null) return;

        float eyeX = aOrigin.X, eyeY = aOrigin.Y, eyeZ = aOrigin.Z + AngleUtil.EyeHeight;
        float range = AngleUtil.Distance3D(eyeX, eyeY, eyeZ, vOrigin.X, vOrigin.Y, vOrigin.Z + AngleUtil.ChestHeight);
        if (range < Config.SilentAim.MinRange) return;

        var st = _scores.GetOrCreate(attacker.Slot, attacker.SteamID, attacker.PlayerName ?? "player", now);

        // First bullet of burst only (recoil exclusion) — flagged in OnWeaponFire.
        if (!st.LastFireWasBurstOpener) return;

        var victimHist = _scores.TryGet(victim.Slot, out var vst) ? vst.Positions : null;
        float lookback = 0.25f;
        int samples = victimHist?.SampleCountInWindow(now, lookback) ?? 0;
        if (samples < 2 && victimHist is not null)
        {
            // Not enough history yet — still compare live body.
        }

        float minErr = victimHist is not null
            ? victimHist.MinViewError(now, lookback, eyeX, eyeY, eyeZ, pitch, yaw,
                vOrigin.X, vOrigin.Y, vOrigin.Z)
            : AngleUtil.ViewErrorToBody(eyeX, eyeY, eyeZ, pitch, yaw, vOrigin.X, vOrigin.Y, vOrigin.Z);

        int hitgroup = 0;
        try { hitgroup = @event.Hitgroup; } catch { /* ignore */ }
        bool head = hitgroup == 1;
        bool precision = WeaponCycle.IsPrecisionWeapon(weapon);
        bool farGrade = head && precision && range >= Config.SilentAim.FarMinRange &&
                        minErr >= Config.SilentAim.FarOffDegrees;
        bool classic = minErr >= Config.SilentAim.OffDegrees;

        if (!classic && !farGrade) return;

        if (classic)
        {
            st.SilentHits++;
            st.SessionSilentHits++;
            if (Config.VerboseConsole)
                Console.WriteLine(
                    $"[YGuardAC] silent-hit {st.Name} vs {victim.PlayerName} err={minErr:F1}° range={range:F0} " +
                    $"w={weapon} session={st.SessionSilentHits}");

            if (st.SilentHits >= Config.SilentAim.MinHits)
            {
                AddScore(attacker, st, "SilentAim", Config.SilentAim.Score, now,
                    $"view {minErr:F1}° off x{st.SilentHits}");
                st.SilentHits = 0;
            }

            if (st.SessionSilentHits >= Config.SilentAim.KickAfterHits && !IsExemptFromPunishment(attacker))
            {
                Console.WriteLine($"[YGuardAC] SILENT-KICK {st.Name} [{st.SteamId}] x{st.SessionSilentHits}");
                NotifyAdmins($"SILENT-KICK {st.Name} steam={st.SteamId} off-view hits={st.SessionSilentHits}");
                KickPlayer(attacker, "YGuardAC: CHEAT DETECTED: silent aim");
                AbortAfterCheat($"{st.Name} silent-aim");
                st.SessionSilentHits = 0;
                return;
            }
        }

        if (farGrade)
        {
            st.FarSilentVictims.Add(victim.Slot);
            AddScore(attacker, st, "SilentAimFar", Config.SilentAim.ScoreFar, now,
                $"far HS {minErr:F1}° @ {range:F0}u vs {victim.PlayerName}", 1f);

            if (st.FarSilentVictims.Count >= Config.SilentAim.FarKickDistinctVictims &&
                !IsExemptFromPunishment(attacker))
            {
                Console.WriteLine(
                    $"[YGuardAC] SILENT-KICK {st.Name} [{st.SteamId}] far-precision victims={st.FarSilentVictims.Count}");
                NotifyAdmins($"SILENT-KICK {st.Name} far-HS distinct={st.FarSilentVictims.Count}");
                KickPlayer(attacker, "YGuardAC: CHEAT DETECTED: silent aim");
                AbortAfterCheat($"{st.Name} silent-aim-far");
                st.FarSilentVictims.Clear();
            }
        }
    }

    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (!Config.Enabled) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (!IsValidHuman(attacker)) return HookResult.Continue;
        if (IsExemptFromDetection(attacker!)) return HookResult.Continue;

        float now = Server.CurrentTime;
        var st = _scores.GetOrCreate(attacker!.Slot, attacker.SteamID, attacker.PlayerName ?? "player", now);
        st.DeathEventsSeen++;

        ScanSmokeEntities(now);

        bool thruSmoke = false;
        int penetrated = 0;
        bool headshot = false;
        try { thruSmoke = @event.Thrusmoke; } catch { /* ignore */ }
        try { penetrated = @event.Penetrated; } catch { /* ignore */ }
        try { headshot = @event.Headshot; } catch { /* ignore */ }

        bool enemyKill = victim is not null && victim.IsValid &&
                         attacker.TeamNum != victim.TeamNum &&
                         attacker.Slot != victim.Slot;

        st.LastKillDebug =
            $"deaths={st.DeathEventsSeen} enemy={enemyKill} teams={attacker.TeamNum}/{victim?.TeamNum} " +
            $"thruSmoke={thruSmoke} pen={penetrated} hs={headshot} smokes={_smokes.Count}";

        if (Config.VerboseConsole)
            Console.WriteLine($"[YGuardAC] kill-debug {st.Name}: {st.LastKillDebug}");

        if (Config.Grief.Enabled && victim is not null && victim.IsValid &&
            attacker.TeamNum == victim.TeamNum && attacker.Slot != victim.Slot)
        {
            st.TeamKills++;
            if (st.TeamKills >= Config.Grief.TeamKillThreshold)
            {
                AddScore(attacker, st, "TeamKill", Config.Grief.TeamKillScore, now,
                    $"tk x{st.TeamKills}");
                st.TeamKills = 0;
            }
        }

        // Legacy large-snap → kill
        if (Config.AimSnap.Enabled && enemyKill &&
            now - st.LastSnapTime <= Config.AimSnap.SnapToKillWindowSeconds)
        {
            st.SnapKillHits++;
            if (st.SnapKillHits >= Config.AimSnap.Occurrences)
            {
                AddScore(attacker, st, "AimSnap", Config.AimSnap.Score, now,
                    $"{st.LastSnapDegrees:F0}° then kill x{st.SnapKillHits}");
                st.SnapKillHits = 0;
            }
        }

        // Snap v2: previous view off target → kill tick on target
        if (Config.AimSnap.Enabled && enemyKill && victim is not null)
            CheckSnapV2(attacker, victim, st, now);

        if (enemyKill && victim is not null)
        {
            CheckSmokeKill(attacker, victim, thruSmoke, st, now);
            CheckWallbangKill(attacker, penetrated, st, now);
            if (headshot && Config.BlindHsBurst.Enabled)
                CheckBlindHsBurst(attacker, victim, st, now);
        }

        ProcessActions(attacker, st, now);
        return HookResult.Continue;
    }

    private void CheckSnapV2(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        PlayerAcState st,
        float now)
    {
        if (!st.HasPrevAngles || !st.HasAngles) return;

        var aPawn = attacker.PlayerPawn.Value;
        var vPawn = victim.PlayerPawn.Value;
        if (aPawn is null || !aPawn.IsValid || vPawn is null || !vPawn.IsValid) return;

        var aOrigin = aPawn.AbsOrigin;
        var vOrigin = vPawn.AbsOrigin;
        if (aOrigin is null || vOrigin is null) return;

        float eyeX = aOrigin.X, eyeY = aOrigin.Y, eyeZ = aOrigin.Z + AngleUtil.EyeHeight;
        float approach = AngleUtil.ViewErrorToBody(
            eyeX, eyeY, eyeZ, st.PrevPitch, st.PrevYaw, vOrigin.X, vOrigin.Y, vOrigin.Z);
        float landing = AngleUtil.ViewErrorToBody(
            eyeX, eyeY, eyeZ, st.Pitch, st.Yaw, vOrigin.X, vOrigin.Y, vOrigin.Z);

        if (approach < Config.AimSnap.ApproachMinDeg) return;
        if (landing > Config.AimSnap.SnapOnTargetMaxDeg) return;

        st.SessionSnapHits++;
        st.SnapKillHits++;
        if (Config.VerboseConsole)
            Console.WriteLine(
                $"[YGuardAC] snap-v2 {st.Name}: approach={approach:F2}° land={landing:F2}° session={st.SessionSnapHits}");

        if (st.SnapKillHits >= Config.AimSnap.Occurrences)
        {
            AddScore(attacker, st, "AimSnap", Config.AimSnap.Score, now,
                $"snap {approach:F1}→{landing:F2}° x{st.SnapKillHits}");
            st.SnapKillHits = 0;
        }

        if (st.SessionSnapHits >= Config.AimSnap.BanAfterMatchHits && !IsExemptFromPunishment(attacker))
        {
            Console.WriteLine($"[YGuardAC] SNAP-KICK {st.Name} [{st.SteamId}] x{st.SessionSnapHits}");
            NotifyAdmins($"SNAP-KICK {st.Name} steam={st.SteamId} precision snaps={st.SessionSnapHits}");
            KickPlayer(attacker, "YGuardAC: CHEAT DETECTED: aim snap");
            AbortAfterCheat($"{st.Name} aim-snap");
            st.SessionSnapHits = 0;
        }
    }

    private void CheckBlindHsBurst(
        CCSPlayerController attacker,
        CCSPlayerController victim,
        PlayerAcState st,
        float now)
    {
        var key = (attacker.Slot, victim.Slot);

        // Spotted at the kill tick → not blind.
        var vPawn = victim.PlayerPawn.Value;
        if (SpottedUtil.IsSpottedBySlot(vPawn, attacker.Slot))
        {
            _lastSeen[key] = now;
            return;
        }

        bool neverSeen = !_lastSeen.ContainsKey(key);
        bool stale = true;
        if (_lastSeen.TryGetValue(key, out float last))
            stale = now - last >= Config.BlindHsBurst.BlindAfterSeconds;

        if (!neverSeen && !stale) return;

        if (!_blindKills.TryGetValue(attacker.Slot, out var list))
            _blindKills[attacker.Slot] = list = new List<(float, int, bool)>();

        list.Add((now, victim.Slot, neverSeen));
        list.RemoveAll(k => now - k.Time > Config.BlindHsBurst.WindowSeconds);

        // Distinct victims in window (stale grade for ≥3; never-seen for early warn).
        var distinctStale = list.Select(k => k.VictimSlot).Distinct().Count();
        var distinctNever = list.Where(k => k.NeverSeen).Select(k => k.VictimSlot).Distinct().Count();

        int scoreFloor = Config.BlindHsBurst.ScoreAtDistinct;
        int kickFloor = Config.BlindHsBurst.KickAtDistinct;

        int scoreDistinct = distinctStale >= 3 ? distinctStale : distinctNever;
        if (scoreDistinct >= scoreFloor && st.BlindScoreTierFired < scoreDistinct)
        {
            st.BlindScoreTierFired = scoreDistinct;
            AddScore(attacker, st, "BlindHsBurst", Config.BlindHsBurst.Score, now,
                $"blind HS distinct={scoreDistinct} (never={distinctNever} stale={distinctStale})", 2f);
            NotifyAdmins($"BlindHS {st.Name} distinct={scoreDistinct} steam={st.SteamId}");
        }

        if (distinctStale >= kickFloor && st.BlindKickFired < distinctStale &&
            !IsExemptFromPunishment(attacker))
        {
            st.BlindKickFired = distinctStale;
            Console.WriteLine(
                $"[YGuardAC] BLIND-HS-KICK {st.Name} [{st.SteamId}] distinct={distinctStale} in {Config.BlindHsBurst.WindowSeconds:F0}s");
            NotifyAdmins($"BLIND-HS-KICK {st.Name} steam={st.SteamId} distinct={distinctStale}");
            KickPlayer(attacker, "YGuardAC: CHEAT DETECTED: blind headshot burst");
            AbortAfterCheat($"{st.Name} blind-hs-burst");
            list.Clear();
        }
    }

    public HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        if (!Config.Enabled || !Config.SmokeKill.Enabled) return HookResult.Continue;

        float expiry = Server.CurrentTime + Config.SmokeKill.DurationSeconds;
        _smokes.Add(new SmokeCloud(@event.X, @event.Y, @event.Z, expiry));
        if (Config.VerboseConsole)
            Console.WriteLine($"[YGuardAC] smoke detonate ({@event.X:F0},{@event.Y:F0},{@event.Z:F0}) total={_smokes.Count}");
        return HookResult.Continue;
    }

    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _smokes.Clear();
        foreach (var st in _scores.Players.Values)
        {
            st.SmokeKills = 0;
            st.WallbangKills = 0;
        }

        return HookResult.Continue;
    }

    private void CheckSmokeKill(CCSPlayerController attacker, CCSPlayerController victim, bool thruSmokeFlag, PlayerAcState st, float now)
    {
        if (!Config.SmokeKill.Enabled) return;

        bool smokeKill = thruSmokeFlag;
        string reason = thruSmokeFlag ? "event.Thrusmoke" : "";

        if (!smokeKill)
        {
            var aPawn = attacker.PlayerPawn.Value;
            var vPawn = victim.PlayerPawn.Value;
            if (aPawn is not null && aPawn.IsValid && vPawn is not null && vPawn.IsValid)
            {
                var aPos = aPawn.AbsOrigin;
                var vPos = vPawn.AbsOrigin;
                if (aPos is not null && vPos is not null)
                {
                    float ax = aPos.X, ay = aPos.Y, az = aPos.Z + 64f;
                    float vx = vPos.X, vy = vPos.Y, vz = vPos.Z + 64f;

                    if (IsSmokeBlockingLos(ax, ay, az, vx, vy, vz))
                    {
                        smokeKill = true;
                        reason = "los-through-smoke";
                    }
                    else if (IsPointInAnySmoke(vx, vy, vz) || IsPointInAnySmoke(vx, vy, vPos.Z))
                    {
                        smokeKill = true;
                        reason = "victim-in-smoke";
                    }
                }
            }
        }

        if (!smokeKill) return;

        st.SessionSmokeHits++;
        st.SmokeKills++;
        st.LastKillDebug += $" | smokeYES:{reason} sessionSmoke={st.SessionSmokeHits}";

        if (st.SmokeKills >= Config.SmokeKill.KillsThreshold)
        {
            bool applied = AddScore(attacker, st, "SmokeKill", Config.SmokeKill.Score, now,
                $"{reason} x{st.SmokeKills}", 0f);
            if (applied)
                st.SmokeKills = 0;
        }

        int kickAfter = Math.Max(1, Config.SmokeKill.BanAfterMatchKills);
        if (st.SessionSmokeHits >= kickAfter && !IsExemptFromPunishment(attacker))
        {
            Console.WriteLine($"[YGuardAC] SMOKE-KICK {st.Name} sessionSmoke={st.SessionSmokeHits}");
            NotifyAdmins($"SMOKE-KICK {st.Name} x{st.SessionSmokeHits} thrusmoke/los");
            KickPlayer(attacker, "YGuardAC: too many smoke kills");
            AbortAfterCheat($"{st.Name} smoke-kills");
            st.SessionSmokeHits = 0;
        }
    }

    private void CheckWallbangKill(CCSPlayerController attacker, int penetrated, PlayerAcState st, float now)
    {
        if (!Config.Wallbang.Enabled) return;
        if (penetrated < Config.Wallbang.MinPenetrations) return;

        st.SessionWallHits++;
        st.WallbangKills++;
        st.LastKillDebug += $" | wallYES:pen={penetrated} sessionWall={st.SessionWallHits}";

        if (st.WallbangKills >= Config.Wallbang.KillsThreshold)
        {
            bool applied = AddScore(attacker, st, "Wallbang", Config.Wallbang.Score, now,
                $"penetrated={penetrated} x{st.WallbangKills}", 0f);
            if (applied)
                st.WallbangKills = 0;
        }

        int kickAfter = Math.Max(1, Config.Wallbang.BanAfterMatchKills);
        if (st.SessionWallHits >= kickAfter && !IsExemptFromPunishment(attacker))
        {
            Console.WriteLine($"[YGuardAC] WALL-KICK {st.Name} sessionWall={st.SessionWallHits}");
            NotifyAdmins($"WALL-KICK {st.Name} x{st.SessionWallHits}");
            KickPlayer(attacker, "YGuardAC: too many wallbang kills");
            AbortAfterCheat($"{st.Name} wallbang");
            st.SessionWallHits = 0;
        }
    }

    private void ScanSmokeEntities(float now)
    {
        if (!Config.SmokeKill.Enabled) return;

        try
        {
            foreach (var smoke in Utilities.FindAllEntitiesByDesignerName<CSmokeGrenadeProjectile>("smokegrenade_projectile"))
            {
                if (smoke is null || !smoke.IsValid) continue;

                bool active = false;
                try { active = smoke.DidSmokeEffect; } catch { /* ignore */ }
                if (!active)
                {
                    try { active = smoke.SmokeEffectTickBegin > 0; } catch { /* ignore */ }
                }
                if (!active) continue;

                float x = 0, y = 0, z = 0;
                try
                {
                    var det = smoke.SmokeDetonationPos;
                    if (det is not null)
                    {
                        x = det.X; y = det.Y; z = det.Z;
                    }
                }
                catch { /* ignore */ }

                if (MathF.Abs(x) < 0.1f && MathF.Abs(y) < 0.1f && MathF.Abs(z) < 0.1f)
                {
                    var origin = smoke.AbsOrigin;
                    if (origin is null) continue;
                    x = origin.X; y = origin.Y; z = origin.Z;
                }

                bool exists = false;
                foreach (var s in _smokes)
                {
                    float dx = s.X - x, dy = s.Y - y, dz = s.Z - z;
                    if (dx * dx + dy * dy + dz * dz < 40f * 40f)
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                    _smokes.Add(new SmokeCloud(x, y, z, now + Config.SmokeKill.DurationSeconds));
            }
        }
        catch (Exception ex)
        {
            if (Config.VerboseConsole)
                Console.WriteLine($"[YGuardAC] smoke scan error: {ex.Message}");
        }
    }

    private bool IsPointInAnySmoke(float x, float y, float z)
    {
        float r = Config.SmokeKill.Radius;
        float r2 = r * r;
        foreach (var s in _smokes)
        {
            float dx = s.X - x, dy = s.Y - y, dz = s.Z - z;
            if (dx * dx + dy * dy + dz * dz <= r2)
                return true;
        }
        return false;
    }

    private bool IsSmokeBlockingLos(float ax, float ay, float az, float vx, float vy, float vz)
    {
        float radius = Config.SmokeKill.Radius;
        float dx = vx - ax, dy = vy - ay, dz = vz - az;
        float lenSq = dx * dx + dy * dy + dz * dz;
        if (lenSq < 1f) return false;

        foreach (var s in _smokes)
        {
            float t = ((s.X - ax) * dx + (s.Y - ay) * dy + (s.Z - az) * dz) / lenSq;
            if (t < 0f || t > 1f) continue;

            float px = ax + t * dx;
            float py = ay + t * dy;
            float pz = az + t * dz;
            float distSq = (s.X - px) * (s.X - px) + (s.Y - py) * (s.Y - py) + (s.Z - pz) * (s.Z - pz);
            if (distSq <= radius * radius)
                return true;
        }

        return false;
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is null || !player.IsValid || player.IsBot || player.IsHLTV) return HookResult.Continue;

        ulong steam = player.SteamID;
        if (player.UserId is not int userId) return HookResult.Continue;
        _ = Task.Run(async () =>
        {
            await Task.Delay(800);
            var (allowed, reason) = await AcGate.CheckAsync(steam);
            if (allowed) return;
            Server.NextFrame(() =>
            {
                var p = Utilities.GetPlayers()
                    .FirstOrDefault(x => x is not null && x.IsValid && x.UserId == userId);
                if (p is null) return;
                AcGate.KickIfDenied(p, reason);
            });
        });
        return HookResult.Continue;
    }

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null && player.IsValid)
        {
            int slot = player.Slot;
            _scores.Remove(slot);
            _blindKills.Remove(slot);
            var keys = _lastSeen.Keys.Where(k => k.Observer == slot || k.Enemy == slot).ToList();
            foreach (var k in keys)
                _lastSeen.Remove(k);
        }
        return HookResult.Continue;
    }

    private bool AddScore(CCSPlayerController player, PlayerAcState st, string module, float amount, float now, string detail, float? cooldownSeconds = null)
    {
        float score = _scores.Add(st, module, amount, now, out bool applied, cooldownSeconds);
        if (!applied) return false;

        if (Config.VerboseConsole)
            Console.WriteLine($"[YGuardAC] {st.Name} [{st.SteamId}] +{amount:F1} ({module}: {detail}) = {score:F1}");

        if (Config.Actions.AnnounceToAdmins && score >= Config.Actions.AlertThreshold)
            NotifyAdmins($" {st.Name} +{amount:F0} {module} → {score:F0}");

        ProcessActions(player, st, now);
        return true;
    }

    private void ProcessActions(CCSPlayerController player, PlayerAcState st, float now)
    {
        if (IsExemptFromPunishment(player)) return;

        var a = Config.Actions;
        if (st.Score >= a.AlertThreshold && !st.AlertSent && a.AnnounceToAdmins)
        {
            st.AlertSent = true;
            NotifyAdmins($"ALERT {st.Name} score={st.Score:F0} steam={st.SteamId}");
        }

        if (st.Score >= a.BanThreshold && st.KickCount >= a.BanMinKicks)
        {
            BanPlayer(player, st, "score-ban");
            return;
        }

        if (st.Score >= a.KickThreshold && now - st.LastKickTime >= a.KickCooldownSeconds)
        {
            st.KickCount++;
            st.LastKickTime = now;
            st.Score = Math.Max(a.AlertThreshold * 0.5f, st.Score * 0.4f);
            Console.WriteLine($"[YGuardAC] KICK {st.Name} [{st.SteamId}] score was high (kicks={st.KickCount})");
            NotifyAdmins($"KICK {st.Name} score high (#{st.KickCount})");
            KickPlayer(player, a.KickReason);
            AbortAfterCheat($"{st.Name} kicked (score)");
        }
    }

    private static void KickPlayer(CCSPlayerController player, string reason)
    {
        string safe = reason.Replace("\"", "'");
        Server.ExecuteCommand($"kickid {player.UserId} {safe}");
    }

    private void BanPlayer(CCSPlayerController player, PlayerAcState st, string reasonTag)
    {
        Console.WriteLine($"[YGuardAC] BAN {st.Name} [{st.SteamId}] score={st.Score:F1} kicks={st.KickCount} ({reasonTag})");
        NotifyAdmins($"BAN {st.Name} steam={st.SteamId} score={st.Score:F0}");

        string cmd = Config.Actions.BanCommand
            .Replace("{userid}", player.UserId.ToString())
            .Replace("{steamid}", st.SteamId.ToString())
            .Replace("{name}", st.Name.Replace("\"", ""))
            .Replace("{score:F0}", st.Score.ToString("F0"))
            .Replace("{score}", st.Score.ToString("F1"));

        try
        {
            Server.ExecuteCommand(cmd);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YGuardAC] BanCommand failed: {ex.Message}");
        }

        KickPlayer(player, Config.Actions.KickReason + " (auto-ban)");
        try
        {
            Server.ExecuteCommand($"banid 0 {st.SteamId}");
            Server.ExecuteCommand("writeid");
        }
        catch
        {
            // ignore
        }

        _scores.Remove(player.Slot);
        AbortAfterCheat($"{st.Name} banned ({reasonTag})");
    }

    private void AbortAfterCheat(string reason)
    {
        if (MatchAbort.AlreadyStarted) return;
        if (!Config.Actions.CancelMatchOnCheat && !Config.Actions.QuitServerOnCheat) return;

        MatchAbort.Begin(Config, reason, (delay, action) =>
        {
            AddTimer(delay, action);
        });
    }

    private void NotifyAdmins(string message)
    {
        foreach (var p in Utilities.GetPlayers())
        {
            if (!IsValidHuman(p)) continue;
            if (!AdminManager.PlayerHasPermissions(p, Config.AdminFlag)) continue;
            p.PrintToChat($" \x02[YGuardAC]\x01 {message}");
        }
    }

    private bool IsExemptFromDetection(CCSPlayerController player)
    {
        if (!Config.ExemptAdminsFromDetection) return false;
        return AdminManager.PlayerHasPermissions(player, Config.AdminFlag);
    }

    private bool IsExemptFromPunishment(CCSPlayerController player)
    {
        if (!Config.ExemptAdmins) return false;
        return AdminManager.PlayerHasPermissions(player, Config.AdminFlag);
    }

    private static bool IsValidHuman(CCSPlayerController? player)
        => player is not null && player.IsValid && !player.IsBot && !player.IsHLTV && player.Connected == PlayerConnectedState.Connected;

    private void OnDebug(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid) return;
        _scores.TryGet(player.Slot, out var st);
        info.ReplyToCommand(
            $"[YGuardAC] v{ModuleVersion} score={(st?.Score ?? 0):F1} peak={(st?.PeakScore ?? 0):F1} " +
            $"sessionSmoke={st?.SessionSmokeHits ?? 0}/{Config.SmokeKill.BanAfterMatchKills} " +
            $"sessionWall={st?.SessionWallHits ?? 0}/{Config.Wallbang.BanAfterMatchKills} " +
            $"silent={st?.SessionSilentHits ?? 0}/{Config.SilentAim.KickAfterHits} " +
            $"snap={st?.SessionSnapHits ?? 0}/{Config.AimSnap.BanAfterMatchHits}");
        info.ReplyToCommand(
            $"[YGuardAC] debug smokeKills={st?.SmokeKills ?? 0} wallKills={st?.WallbangKills ?? 0} " +
            $"farSilentVictims={st?.FarSilentVictims.Count ?? 0} " +
            $"activeSmokes={_smokes.Count} deathsSeen={st?.DeathEventsSeen ?? 0}");
        info.ReplyToCommand($"[YGuardAC] lastKill: {st?.LastKillDebug ?? "none"}");
    }

    private void OnSelfScore(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid) return;
        if (!_scores.TryGet(player.Slot, out var st))
        {
            info.ReplyToCommand("[YGuardAC] No score yet.");
            return;
        }

        _scores.Decay(st, Server.CurrentTime);
        info.ReplyToCommand($"[YGuardAC] score={st.Score:F1} peak={st.PeakScore:F1} kicks={st.KickCount}");
    }

    private void OnInspectScore(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not null && !AdminManager.PlayerHasPermissions(player, Config.AdminFlag))
        {
            info.ReplyToCommand("[YGuardAC] No permission.");
            return;
        }

        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int userId))
        {
            info.ReplyToCommand("Usage: css_ygac_score <userid>");
            return;
        }

        var target = Utilities.GetPlayers().FirstOrDefault(p => p.UserId == userId);
        if (target is null || !_scores.TryGet(target.Slot, out var st))
        {
            info.ReplyToCommand("[YGuardAC] Player not tracked.");
            return;
        }

        _scores.Decay(st, Server.CurrentTime);
        info.ReplyToCommand($"[YGuardAC] {st.Name} score={st.Score:F1} peak={st.PeakScore:F1} kicks={st.KickCount} steam={st.SteamId}");
    }

    private void OnResetScore(CCSPlayerController? player, CommandInfo info)
    {
        if (player is not null && !AdminManager.PlayerHasPermissions(player, Config.AdminFlag))
        {
            info.ReplyToCommand("[YGuardAC] No permission.");
            return;
        }

        if (info.ArgCount < 2 || !int.TryParse(info.GetArg(1), out int userId))
        {
            info.ReplyToCommand("Usage: css_ygac_reset <userid>");
            return;
        }

        var target = Utilities.GetPlayers().FirstOrDefault(p => p.UserId == userId);
        if (target is null)
        {
            info.ReplyToCommand("[YGuardAC] Player not found.");
            return;
        }

        _scores.Remove(target.Slot);
        info.ReplyToCommand($"[YGuardAC] Reset score for userid {userId}");
    }
}
