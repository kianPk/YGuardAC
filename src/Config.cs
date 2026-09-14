using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace YGuardAC;

public sealed class YGuardACConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("VerboseConsole")] public bool VerboseConsole { get; set; } = true;
    [JsonPropertyName("ExemptAdmins")] public bool ExemptAdmins { get; set; } = true;
    [JsonPropertyName("AdminFlag")] public string AdminFlag { get; set; } = "@css/ban";

    [JsonPropertyName("Score")] public ScoreConfig Score { get; set; } = new();
    [JsonPropertyName("Actions")] public ActionsConfig Actions { get; set; } = new();
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
    public float DecayPerSecond { get; set; } = 0.6f;
    public float FloorPercentOfPeak { get; set; } = 0.08f;
    public float ModuleCooldownSeconds { get; set; } = 1f;
}

public sealed class ActionsConfig
{
    public float AlertThreshold { get; set; } = 40f;
    public float KickThreshold { get; set; } = 90f;
    public float BanThreshold { get; set; } = 90f;
    public float KickCooldownSeconds { get; set; } = 300f;
    /// <summary>0 = ban as soon as BanThreshold is reached (checked before kick).</summary>
    public int BanMinKicks { get; set; } = 0;
    public bool AnnounceToAdmins { get; set; } = true;
    public string BanCommand { get; set; } =
        "css_ban #{userid} 0 \"YGuardAC auto-ban (score {score:F0})\"";
    public string KickReason { get; set; } = "YGuardAC: suspicion score too high";
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
    /// <summary>Smoke particle radius used for LOS blocking (CS smoke ~144-175u).</summary>
    public float Radius { get; set; } = 150f;
    public float DurationSeconds { get; set; } = 18f;
    public int KillsThreshold { get; set; } = 3;
    public float Score { get; set; } = 6f;
}

public sealed class WallbangConfig
{
    public bool Enabled { get; set; } = true;
    /// <summary>Minimum walls penetrated on the killing shot (game event Penetrated).</summary>
    public int MinPenetrations { get; set; } = 1;
    public int KillsThreshold { get; set; } = 3;
    public float Score { get; set; } = 6f;
}
