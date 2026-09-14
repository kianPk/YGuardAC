using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;

namespace YGuardAC;

public sealed class YGuardACPlugin : BasePlugin, IPluginConfig<YGuardACConfig>
{
    public override string ModuleName => "YGuardAC";
    public override string ModuleVersion => "1.2.1";
    public override string ModuleAuthor => "yguard";
    public override string ModuleDescription => "Suspicion-score anti-cheat with kick/ban thresholds";

    public YGuardACConfig Config { get; set; } = new();

    private ScoreManager _scores = null!;
    private float _lastTickTime;
    private float _lastSmokeScan;
    private readonly List<SmokeCloud> _smokes = new();

    private readonly record struct SmokeCloud(float X, float Y, float Z, float Expiry);

    public void OnConfigParsed(YGuardACConfig config)
    {
        MigrateLegacyConfig(config);
        Config = config;
        _scores = new ScoreManager(config.Score);
        Console.WriteLine(
            $"[YGuardAC] config ready decay={config.Score.DecayPerSecond:F2} banAt={config.Actions.BanThreshold:F0} " +
            $"smokeScore={config.SmokeKill.Score:F0} smokeBanAfter={config.SmokeKill.BanAfterMatchKills}");
    }

    /// <summary>
    /// Old JSON from first install kept decay=0.6 / ban=90 and blocked real bans.
    /// Force sane values whenever we detect that legacy profile.
    /// </summary>
    private static void MigrateLegacyConfig(YGuardACConfig c)
    {
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

        if (c.SmokeKill.Score < 12f)
            c.SmokeKill.Score = 15f;
        if (c.SmokeKill.KillsThreshold > 1)
            c.SmokeKill.KillsThreshold = 1;
        c.SmokeKill.CooldownSeconds = 0f;
        if (c.SmokeKill.BanAfterMatchKills <= 0)
            c.SmokeKill.BanAfterMatchKills = 5;

        if (c.Wallbang.Score < 12f)
            c.Wallbang.Score = 15f;
        if (c.Wallbang.KillsThreshold > 1)
            c.Wallbang.KillsThreshold = 1;
        c.Wallbang.CooldownSeconds = 0f;
        if (c.Wallbang.BanAfterMatchKills <= 0)
            c.Wallbang.BanAfterMatchKills = 5;
    }

    public override void Load(bool hotReload)
    {
        _scores ??= new ScoreManager(Config.Score);
        RegisterListener<Listeners.OnTick>(OnTick);

        // Explicit registration is more reliable than attributes on some CSS builds.
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);

        AddCommand("css_ygac", "Show your YGuardAC score", OnSelfScore);
        AddCommand("css_ygac_score", "Inspect a player score by userid", OnInspectScore);
        AddCommand("css_ygac_reset", "Reset a player score by userid", OnResetScore);
        AddCommand("css_ygac_debug", "Debug smoke/wallbang counters", OnDebug);

        Console.WriteLine("[YGuardAC] Loaded v1.2.1 — cancel match before quit; cache match id.");

        // Warm match-id cache so cancel still works after kick.
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
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnTick>(OnTick);
        _scores.Clear();
        _smokes.Clear();
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

        foreach (var player in Utilities.GetPlayers())
        {
            if (!IsValidHuman(player)) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn is null || !pawn.IsValid) continue;

            var st = _scores.GetOrCreate(player.Slot, player.SteamID, player.PlayerName ?? "player", now);
            if (st.ScoreWindowStart < 0f)
                st.ScoreWindowStart = now;

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

    private void CheckAnglesAndMovement(CCSPlayerController player, CCSPlayerPawn pawn, PlayerAcState st, float now, float dt)
    {
        // Ground / bhop
        bool onGround = (pawn.Flags & (uint)PlayerFlags.FL_ONGROUND) != 0;
        if (Config.BunnyHop.Enabled)
        {
            if (onGround)
            {
                st.GroundTicks++;
            }
            else if (st.OnGround && !onGround)
            {
                // left ground
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

        // Speedhack (2D)
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
        if (!Config.Enabled || !Config.RapidFire.Enabled) return HookResult.Continue;

        var player = @event.Userid;
        if (!IsValidHuman(player) || IsExemptFromDetection(player!)) return HookResult.Continue;

        float now = Server.CurrentTime;
        var st = _scores.GetOrCreate(player!.Slot, player.SteamID, player.PlayerName ?? "player", now);
        string weapon = @event.Weapon ?? "";

        if (WeaponCycle.TryGetCycle(weapon, out float cycle) &&
            st.LastWeapon == weapon &&
            st.LastFireTime > 0f)
        {
            float gap = now - st.LastFireTime;
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
        if (!Config.Enabled || !Config.Grief.Enabled) return HookResult.Continue;

        var attacker = @event.Attacker;
        var victim = @event.Userid;
        if (!IsValidHuman(attacker) || victim is null || !victim.IsValid) return HookResult.Continue;
        if (IsExemptFromDetection(attacker!)) return HookResult.Continue;
        if (attacker!.TeamNum != victim.TeamNum || attacker.Slot == victim.Slot) return HookResult.Continue;

        float now = Server.CurrentTime;
        var st = _scores.GetOrCreate(attacker.Slot, attacker.SteamID, attacker.PlayerName ?? "player", now);
        st.TeamDamage += @event.DmgHealth;
        if (st.TeamDamage >= Config.Grief.TeamDamageHp)
        {
            AddScore(attacker, st, "TeamDamage", Config.Grief.TeamDamageScore, now,
                $"{st.TeamDamage:F0} HP");
            st.TeamDamage = 0;
        }

        return HookResult.Continue;
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
        try { thruSmoke = @event.Thrusmoke; } catch { /* ignore */ }
        try { penetrated = @event.Penetrated; } catch { /* ignore */ }

        bool enemyKill = victim is not null && victim.IsValid &&
                         attacker.TeamNum != victim.TeamNum &&
                         attacker.Slot != victim.Slot;

        st.LastKillDebug =
            $"deaths={st.DeathEventsSeen} enemy={enemyKill} teams={attacker.TeamNum}/{victim?.TeamNum} " +
            $"thruSmoke={thruSmoke} pen={penetrated} smokes={_smokes.Count} " +
            $"victimBot={(victim?.IsBot ?? false)}";

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

        if (enemyKill && victim is not null)
        {
            CheckSmokeKill(attacker, victim, thruSmoke, st, now);
            CheckWallbangKill(attacker, penetrated, st, now);
        }

        ProcessActions(attacker, st, now);
        return HookResult.Continue;
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
                        // Kill while victim is inside the cloud (common "smoke kill").
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

        // Always apply score with zero cooldown for smoke (ignore stale global cooldown).
        if (st.SmokeKills >= Config.SmokeKill.KillsThreshold)
        {
            bool applied = AddScore(attacker, st, "SmokeKill", Config.SmokeKill.Score, now,
                $"{reason} x{st.SmokeKills}", 0f);
            if (applied)
                st.SmokeKills = 0;
        }

        int banAfter = Math.Max(1, Config.SmokeKill.BanAfterMatchKills);
        if (st.SessionSmokeHits >= banAfter && !IsExemptFromPunishment(attacker))
        {
            Console.WriteLine($"[YGuardAC] SMOKE-BAN {st.Name} sessionSmoke={st.SessionSmokeHits}");
            NotifyAdmins($"SMOKE-BAN {st.Name} x{st.SessionSmokeHits} thrusmoke/los");
            BanPlayer(attacker, st, "smoke-ban");
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

        int banAfter = Math.Max(1, Config.Wallbang.BanAfterMatchKills);
        if (st.SessionWallHits >= banAfter && !IsExemptFromPunishment(attacker))
        {
            Console.WriteLine($"[YGuardAC] WALL-BAN {st.Name} sessionWall={st.SessionWallHits}");
            NotifyAdmins($"WALL-BAN {st.Name} x{st.SessionWallHits}");
            BanPlayer(attacker, st, "wall-ban");
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

                // Avoid duplicates near the same cloud.
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

    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player is not null && player.IsValid)
            _scores.Remove(player.Slot);
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

        // Ban is checked first. With BanMinKicks=0, crossing BanThreshold bans immediately.
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
        => player is not null && player.IsValid && !player.IsBot && !player.IsHLTV && player.Connected == PlayerConnectedState.PlayerConnected;

    private void OnDebug(CCSPlayerController? player, CommandInfo info)
    {
        if (player is null || !player.IsValid) return;
        _scores.TryGet(player.Slot, out var st);
        info.ReplyToCommand(
            $"[YGuardAC] v{ModuleVersion} score={(st?.Score ?? 0):F1} peak={(st?.PeakScore ?? 0):F1} " +
            $"sessionSmoke={st?.SessionSmokeHits ?? 0}/{Config.SmokeKill.BanAfterMatchKills} " +
            $"sessionWall={st?.SessionWallHits ?? 0}/{Config.Wallbang.BanAfterMatchKills}");
        info.ReplyToCommand(
            $"[YGuardAC] debug smokeKills={st?.SmokeKills ?? 0} wallKills={st?.WallbangKills ?? 0} " +
            $"activeSmokes={_smokes.Count} deathsSeen={st?.DeathEventsSeen ?? 0} " +
            $"decay={Config.Score.DecayPerSecond:F2} banAt={Config.Actions.BanThreshold:F0} smokePts={Config.SmokeKill.Score:F0}");
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
