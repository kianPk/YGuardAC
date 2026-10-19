using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace YGuardAC;

internal static class AngleUtil
{
    public const float EyeHeight = 64f;
    public const float HeadHeight = 72f;
    public const float ChestHeight = 48f;

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

    /// <summary>Angle from eye→target (world) versus current view pitch/yaw.</summary>
    public static float ViewErrorToPoint(
        float eyeX, float eyeY, float eyeZ,
        float pitch, float yaw,
        float tx, float ty, float tz)
    {
        float dx = tx - eyeX;
        float dy = ty - eyeY;
        float dz = tz - eyeZ;
        float horiz = MathF.Sqrt(dx * dx + dy * dy);
        if (horiz < 0.001f && MathF.Abs(dz) < 0.001f)
            return 0f;

        float targetYaw = MathF.Atan2(dy, dx) * (180f / MathF.PI);
        float targetPitch = -MathF.Atan2(dz, horiz) * (180f / MathF.PI);
        return AngleDelta(pitch, yaw, targetPitch, targetYaw);
    }

    public static float ViewErrorToBody(
        float eyeX, float eyeY, float eyeZ,
        float pitch, float yaw,
        float originX, float originY, float originZ)
    {
        float head = ViewErrorToPoint(eyeX, eyeY, eyeZ, pitch, yaw, originX, originY, originZ + HeadHeight);
        float chest = ViewErrorToPoint(eyeX, eyeY, eyeZ, pitch, yaw, originX, originY, originZ + ChestHeight);
        float hips = ViewErrorToPoint(eyeX, eyeY, eyeZ, pitch, yaw, originX, originY, originZ + 24f);
        return MathF.Min(head, MathF.Min(chest, hips));
    }

    public static float Distance2D(float ax, float ay, float bx, float by)
    {
        float dx = bx - ax, dy = by - ay;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    public static float Distance3D(float ax, float ay, float az, float bx, float by, float bz)
    {
        float dx = bx - ax, dy = by - ay, dz = bz - az;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

/// <summary>Ring buffer of recent world positions for lag-comp style silent-aim checks.</summary>
internal sealed class PositionHistory
{
    private readonly (float T, float X, float Y, float Z)[] _buf;
    private int _count;
    private int _head;

    public PositionHistory(int capacity = 32)
    {
        _buf = new (float, float, float, float)[Math.Max(4, capacity)];
    }

    public void Push(float t, float x, float y, float z)
    {
        _buf[_head] = (t, x, y, z);
        _head = (_head + 1) % _buf.Length;
        if (_count < _buf.Length) _count++;
    }

    public void Clear()
    {
        _count = 0;
        _head = 0;
    }

    /// <summary>Minimum view error to any sample within lookbackSeconds (and optional live point).</summary>
    public float MinViewError(
        float now, float lookbackSeconds,
        float eyeX, float eyeY, float eyeZ,
        float pitch, float yaw,
        float? liveX = null, float? liveY = null, float? liveZ = null)
    {
        float min = float.MaxValue;
        if (liveX is float lx && liveY is float ly && liveZ is float lz)
            min = MathF.Min(min, AngleUtil.ViewErrorToBody(eyeX, eyeY, eyeZ, pitch, yaw, lx, ly, lz));

        int n = _count;
        for (int i = 0; i < n; i++)
        {
            int idx = (_head - 1 - i + _buf.Length * 4) % _buf.Length;
            var s = _buf[idx];
            if (now - s.T > lookbackSeconds) break;
            float err = AngleUtil.ViewErrorToBody(eyeX, eyeY, eyeZ, pitch, yaw, s.X, s.Y, s.Z);
            if (err < min) min = err;
        }

        return min == float.MaxValue ? 999f : min;
    }

    public int SampleCountInWindow(float now, float lookbackSeconds)
    {
        int n = 0;
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head - 1 - i + _buf.Length * 4) % _buf.Length;
            if (now - _buf[idx].T > lookbackSeconds) break;
            n++;
        }
        return n;
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

    private static readonly HashSet<string> ExcludedSilent = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_xm1014", "weapon_nova", "weapon_mag7", "weapon_sawedoff",
        "weapon_hegrenade", "weapon_flashbang", "weapon_smokegrenade",
        "weapon_molotov", "weapon_incgrenade", "weapon_decoy", "weapon_taser",
        "weapon_knife", "weapon_knife_t", "weapon_bayonet",
    };

    private static readonly HashSet<string> PrecisionWeapons = new(StringComparer.OrdinalIgnoreCase)
    {
        "weapon_deagle", "weapon_revolver", "weapon_usp_silencer", "weapon_hkp2000",
        "weapon_glock", "weapon_p250", "weapon_fiveseven", "weapon_elite",
        "weapon_awp", "weapon_ssg08",
    };

    public static bool TryGetCycle(string? weapon, out float seconds)
    {
        seconds = 0f;
        if (string.IsNullOrWhiteSpace(weapon)) return false;
        return Rates.TryGetValue(Normalize(weapon), out seconds);
    }

    public static bool IsExcludedFromSilentAim(string? weapon)
        => !string.IsNullOrWhiteSpace(weapon) && ExcludedSilent.Contains(Normalize(weapon));

    public static bool IsPrecisionWeapon(string? weapon)
        => !string.IsNullOrWhiteSpace(weapon) && PrecisionWeapons.Contains(Normalize(weapon));

    public static string Normalize(string weapon)
    {
        weapon = weapon.Trim();
        if (!weapon.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase))
            weapon = "weapon_" + weapon;
        return weapon;
    }
}

internal static class SpottedUtil
{
    /// <summary>
    /// Best-effort: enemy appears in observer's spotted mask (slot bit).
    /// Returns false when the schema field is missing on this CSS build.
    /// </summary>
    public static bool IsSpottedBySlot(CCSPlayerPawn? enemyPawn, int observerSlot)
    {
        if (enemyPawn is null || !enemyPawn.IsValid) return false;
        if (observerSlot < 0 || observerSlot >= 64) return false;

        try
        {
            var spotted = enemyPawn.EntitySpottedState;
            int word = observerSlot / 32;
            int bit = observerSlot % 32;
            var maskArr = spotted.SpottedByMask;
            if (word < 0 || word >= maskArr.Length) return false;
            uint mask = maskArr[word];
            return (mask & (1u << bit)) != 0;
        }
        catch
        {
            return false;
        }
    }
}
