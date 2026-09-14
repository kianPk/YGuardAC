using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace YGuardAC;

public sealed class YGuardACConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("VerboseConsole")] public bool VerboseConsole { get; set; } = true;
    /// <summary>Skip kick/ban for admins. Detection/score still runs unless ExemptAdminsFromDetection.</summary>
    [JsonPropertyName("ExemptAdmins")] public bool ExemptAdmins { get; set; } = true;
    /// <summary>If true, admins get no score at all (makes testing as admin look like score=0 forever).</summary>
    [JsonPropertyName("ExemptAdminsFromDetection")] public bool ExemptAdminsFromDetection { get; set; } = false;
    [JsonPropertyName("AdminFlag")] public string AdminFlag { get; set; } = "@css/ban";

    [JsonPropertyName("Score")] public ScoreConfig Score { get; set; } = new();
    [JsonPropertyName("Actions")] public ActionsConfig Actions { get; set; } = new();
    /// <summary>Reset each player's score / session counters after this many minutes in the match.</summary>
    [JsonPropertyName("ScoreResetMinutes")] public float ScoreResetMinutes { get; set; } = 10f;
    [JsonPropertyName("RapidFire")] public RapidFireConfig RapidFire { get; set; } = new();
    [JsonPropertyName("Speedhack")] public SpeedhackConfig Speedhack { get; set; } = new();
    [JsonPropertyName("BunnyHop")] public BunnyHopConfig BunnyHop { get; set; } = new();
    [JsonPropertyName("Spinbot")] public SpinbotConfig Spinbot { get; set; } = new();
    [JsonPropertyName("UntrustedAngles")] public UntrustedAnglesConfig UntrustedAngles { get; set; } = new();
    [JsonPropertyName("AimSnap")] public AimSnapConfig AimSnap { get; set; } = new();
    [JsonPropertyName("Grief")] public GriefConfig Grief { get; set; } = new();
    [JsonPropertyName("SmokeKill")] public SmokeKillConfig SmokeKill { get; set; } = new();
    [JsonPropertyName("Wallbang")] public WallbangConfig Wallbang { get; set; } = new();
}

public sealed class ScoreConfig
{
    public float MaxScore { get; set; } = 200f;
    public float MaxSingleAddition { get; set; } = 20f;
    public float DecayPerSecond { get; set; } = 0.15f;
    public float FloorPercentOfPeak { get; set; } = 0f;
    public float ModuleCooldownSeconds { get; set; } = 0.25f;
}

public sealed class ActionsConfig
{
    public float AlertThreshold { get; set; } = 30f;
    public float KickThreshold { get; set; } = 50f;
    public float BanThreshold { get; set; } = 50f;
    public float KickCooldownSeconds { get; set; } = 300f;
    /// <summary>0 = ban as soon as BanThreshold is reached (checked before kick).</summary>
    public int BanMinKicks { get; set; } = 0;
    public bool AnnounceToAdmins { get; set; } = true;
    public string BanCommand { get; set; } =
        "css_ban #{userid} 0 \"YGuardAC auto-ban (score {score:F0})\"";
    public string KickReason { get; set; } = "YGuardAC: suspicion score too high";

    /// <summary>After a cheat kick/ban, mark the 5stack match Canceled via GraphQL.</summary>
    public bool CancelMatchOnCheat { get; set; } = true;
    /// <summary>After a cheat kick/ban, quit the CS2 process (ends the match pod).</summary>
    public bool QuitServerOnCheat { get; set; } = true;
    public float AbortDelaySeconds { get; set; } = 6f;
    /// <summary>Optional. Falls back to env HASURA_GRAPHQL_ADMIN_SECRET.</summary>
    public string HasuraAdminSecret { get; set; } = "";
    /// <summary>Optional. Defaults to {API_DOMAIN}/v1/graphql.</summary>
    public string GraphqlUrl { get; set; } = "";
}

public sealed class RapidFireConfig
{
    public bool Enabled { get; set; } = true;
    public float ToleranceMultiplier { get; set; } = 0.82f;
    public int Threshold { get; set; } = 6;
    public float Score { get; set; } = 8f;
}

public sealed class SpeedhackConfig
{
    public bool Enabled { get; set; } = true;
    public float MaxSpeed { get; set; } = 338f;
    public int ConsecutiveTicks { get; set; } = 35;
    public float Score { get; set; } = 10f;
}

public sealed class BunnyHopConfig
{
    public bool Enabled { get; set; } = true;
    public int PerfectChain { get; set; } = 14;
    public int MaxGroundTicks { get; set; } = 3;
    public float Score { get; set; } = 4f;
}

public sealed class SpinbotConfig
{
    public bool Enabled { get; set; } = true;
    public float MinDegPerSecond { get; set; } = 2200f;
    public int ConsecutiveTicks { get; set; } = 20;
    public float Score { get; set; } = 15f;
}

public sealed class UntrustedAnglesConfig
{
    public bool Enabled { get; set; } = true;
    public float MaxPitch { get; set; } = 89.5f;
    public float Score { get; set; } = 12f;
}

public sealed class AimSnapConfig
{
    public bool Enabled { get; set; } = true;
    public float MinSnapDegrees { get; set; } = 65f;
    public float SnapToKillWindowSeconds { get; set; } = 0.25f;
    public int Occurrences { get; set; } = 6;
    public float Score { get; set; } = 3.5f;
}

public sealed class GriefConfig
{
    public bool Enabled { get; set; } = true;
    public int TeamKillThreshold { get; set; } = 3;
    public float TeamKillScore { get; set; } = 5f;
    public int TeamDamageHp { get; set; } = 400;
    public float TeamDamageScore { get; set; } = 3f;
}

public sealed class SmokeKillConfig
{
    public bool Enabled { get; set; } = true;
    public float Radius { get; set; } = 175f;
    public float DurationSeconds { get; set; } = 22f;
    public int KillsThreshold { get; set; } = 1;
    public float Score { get; set; } = 15f;
    /// <summary>0 = no cooldown between scored smoke kills.</summary>
    public float CooldownSeconds { get; set; } = 0f;
    /// <summary>Ban after this many smoke kills in the current match (ignores score decay).</summary>
    public int BanAfterMatchKills { get; set; } = 5;
}

public sealed class WallbangConfig
{
    public bool Enabled { get; set; } = true;
    public int MinPenetrations { get; set; } = 1;
    public int KillsThreshold { get; set; } = 1;
    public float Score { get; set; } = 15f;
    public float CooldownSeconds { get; set; } = 0f;
    public int BanAfterMatchKills { get; set; } = 5;
}
