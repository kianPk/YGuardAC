using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace YGuardAC;

internal static class AngleUtil
{
    /// <summary>
    /// Prefer V_angle (stable after EyeAngles schema break). See CSS issue #1023.
    /// </summary>
    public static bool TryGetViewAngles(CCSPlayerPawn pawn, out float pitch, out float yaw)
    {
        pitch = 0f;
        yaw = 0f;
        try
        {
            QAngle? a = pawn.V_angle;
            if (a is null) return false;
            pitch = a.X;
            yaw = a.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static float YawDelta(float from, float to)
    {
        float d = to - from;
        while (d > 180f) d -= 360f;
        while (d < -180f) d += 360f;
        return d;
    }

    public static float AngleDelta(float pitchA, float yawA, float pitchB, float yawB)
    {
        float dp = pitchB - pitchA;
        float dy = YawDelta(yawA, yawB);
        return MathF.Sqrt(dp * dp + dy * dy);
    }
}

internal static class WeaponCycle
{
    // Approximate CS2 cycle times in seconds (primary fire).
    private static readonly Dictionary<string, float> Rates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["weapon_deagle"] = 0.225f,
        ["weapon_revolver"] = 0.5f,
        ["weapon_glock"] = 0.15f,
        ["weapon_hkp2000"] = 0.17f,
        ["weapon_usp_silencer"] = 0.17f,
        ["weapon_p250"] = 0.15f,
        ["weapon_fiveseven"] = 0.15f,
        ["weapon_tec9"] = 0.12f,
        ["weapon_cz75a"] = 0.1f,
        ["weapon_elite"] = 0.12f,
        ["weapon_ak47"] = 0.1f,
        ["weapon_m4a1"] = 0.09f,
        ["weapon_m4a1_silencer"] = 0.1f,
        ["weapon_aug"] = 0.09f,
        ["weapon_sg556"] = 0.09f,
        ["weapon_famas"] = 0.09f,
        ["weapon_galilar"] = 0.09f,
        ["weapon_awp"] = 1.455f,
        ["weapon_ssg08"] = 1.25f,
        ["weapon_scar20"] = 0.25f,
        ["weapon_g3sg1"] = 0.25f,
        ["weapon_mp9"] = 0.07f,
        ["weapon_mac10"] = 0.075f,
        ["weapon_mp7"] = 0.08f,
        ["weapon_mp5sd"] = 0.08f,
        ["weapon_ump45"] = 0.09f,
        ["weapon_p90"] = 0.07f,
        ["weapon_bizon"] = 0.08f,
        ["weapon_nova"] = 0.88f,
        ["weapon_xm1014"] = 0.35f,
        ["weapon_sawedoff"] = 0.85f,
        ["weapon_mag7"] = 0.85f,
        ["weapon_negev"] = 0.075f,
        ["weapon_m249"] = 0.08f,
    };

    public static bool TryGetCycle(string? weapon, out float seconds)
    {
        seconds = 0f;
        if (string.IsNullOrWhiteSpace(weapon)) return false;
        return Rates.TryGetValue(weapon, out seconds);
    }
}
