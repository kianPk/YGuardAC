namespace YGuardAC;

internal sealed class PlayerAcState
{
    public ulong SteamId;
    public string Name = "";
    public float Score;
    public float PeakScore;
    public float LastUpdateTime;
    public int KickCount;
    public float LastKickTime = -9999f;
    public bool AlertSent;

    public float Pitch;
    public float Yaw;
    public bool HasAngles;
    public bool OnGround;
    public int GroundTicks;
    public int PerfectBhopChain;
    public int SpeedOverTicks;
    public int SpinTicks;
    public int RapidFireHits;
    public int TeamKills;
    public float TeamDamage;
    public int SnapKillHits;
    public int SmokeKills;
    public int WallbangKills;

    public string? LastWeapon;
    public float LastFireTime = -9999f;
    public float LastSnapTime = -9999f;
    public float LastSnapDegrees;

    public readonly Dictionary<string, float> ModuleCooldownUntil = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ScoreManager
{
    private readonly Dictionary<int, PlayerAcState> _players = new();
    private readonly ScoreConfig _cfg;

    public ScoreManager(ScoreConfig cfg) => _cfg = cfg;

    public IReadOnlyDictionary<int, PlayerAcState> Players => _players;

    public PlayerAcState GetOrCreate(int slot, ulong steamId, string name, float now)
    {
        if (!_players.TryGetValue(slot, out var st))
        {
            st = new PlayerAcState { SteamId = steamId, Name = name, LastUpdateTime = now };
            _players[slot] = st;
        }
        else
        {
            st.SteamId = steamId;
            st.Name = name;
        }

        Decay(st, now);
        return st;
    }

    public bool TryGet(int slot, out PlayerAcState st) => _players.TryGetValue(slot, out st!);

    public void Remove(int slot) => _players.Remove(slot);

    public void Clear() => _players.Clear();

    public void Decay(PlayerAcState st, float now)
    {
        float dt = Math.Max(0f, now - st.LastUpdateTime);
        st.LastUpdateTime = now;
        if (dt <= 0f || st.Score <= 0f) return;

        float floor = st.PeakScore * _cfg.FloorPercentOfPeak;
        st.Score = Math.Max(floor, st.Score - _cfg.DecayPerSecond * dt);
    }

    public float Add(PlayerAcState st, string module, float amount, float now, out bool applied)
    {
        Decay(st, now);
        applied = false;

        if (st.ModuleCooldownUntil.TryGetValue(module, out float until) && now < until)
            return st.Score;

        float add = Math.Clamp(amount, 0f, _cfg.MaxSingleAddition);
        st.Score = Math.Min(_cfg.MaxScore, st.Score + add);
        st.PeakScore = Math.Max(st.PeakScore, st.Score);
        st.ModuleCooldownUntil[module] = now + _cfg.ModuleCooldownSeconds;
        applied = true;
        return st.Score;
    }
}
