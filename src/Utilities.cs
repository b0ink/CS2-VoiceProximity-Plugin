using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace ProximityChat;

public partial class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public void CheckAdmin(SteamID? steamId)
    {
        if (steamId?.SteamId64 == null)
        {
            return;
        }

        var steamId64 = steamId.SteamId64;
        if (!PlayerData.ContainsKey((ulong)steamId64))
        {
            PlayerData[steamId64] = new PlayerData(steamId64.ToString(), $"{steamId64}");
        }

        PlayerData[steamId64].IsAdmin = false;

        var adminFlags = Config.ServerConfigAdmins.Split(",").ToList();
        if (adminFlags != null)
        {
            foreach (var flag in adminFlags)
            {
                if (AdminManager.PlayerHasPermissions(steamId, flag) || AdminManager.PlayerInGroup(steamId, flag))
                {
                    PlayerData[steamId64].IsAdmin = true;
                }
            }
        }
    }

    public CBaseEntity? GetObserverEntity(CCSPlayerController? observer)
    {
        if (!IsValid(observer))
        {
            return null;
        }

        var observerPawn = observer!.ObserverPawn?.Value;
        if (observerPawn == null)
        {
            return null;
        }

        var observedEntity = observerPawn.ObserverServices?.ObserverTarget?.Value;
        if (observedEntity != null && observedEntity.IsValid)
        {
            return observedEntity;
        }

        return null;
    }

    public CCSPlayerController? GetObserverTarget(CCSPlayerController? observer)
    {
        var observedEntity = GetObserverEntity(observer);
        if (observedEntity == null || !observedEntity.IsValid)
        {
            return null;
        }

        if (observedEntity.DesignerName != "player")
        {
            return null;
        }

        var observedPlayerPawn = observedEntity.As<CCSPlayerPawn>();

        if (observedPlayerPawn != null && observedPlayerPawn.IsValid)
        {
            var controller = observedPlayerPawn.Controller.Value;
            if (controller == null)
            {
                return null;
            }

            var playerController = controller.As<CCSPlayerController>();
            if (IsValid(playerController))
            {
                return playerController;
            }
        }

        return null;
    }

    public ObserverMode_t? GetObserverMode(CCSPlayerController? observer)
    {
        if (observer == null || !IsValid(observer))
        {
            return ObserverMode_t.OBS_MODE_NONE;
        }

        var observerServices = observer.Pawn.Value?.ObserverServices;
        if (observerServices != null)
        {
            var observerMode = observerServices.ObserverMode;
            return (ObserverMode_t)observerMode;
        }
        return ObserverMode_t.OBS_MODE_NONE;
    }

    public bool IsValid(CCSPlayerController? playerController)
    {
        if (playerController == null)
            return false;
        if (playerController.IsValid == false)
            return false;
        if (playerController.IsHLTV)
            return false;
        if (playerController.Connected != PlayerConnectedState.Connected)
            return false;
        if (playerController.PlayerPawn?.Value == null)
            return false;
        if (playerController.PlayerPawn.IsValid == false)
            return false;

        return true;
    }

    public bool IsAlive(CCSPlayerController? player)
    {
        if (player != null && IsValid(player))
        {
            if (player!.Pawn.Value!.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                return true;
            }
        }
        return false;
    }

    private const float LookAtDistance = 8192f;

    // Managed equivalent of NativeAPI.AngleVectors' forward output (Source angles in degrees).
    // The native call needs QAngle/Vector handles, which CounterStrikeSharp tracks until the objects are collected.
    public static void GetForwardVector(float pitchDegrees, float yawDegrees, out float x, out float y, out float z)
    {
        var (sinPitch, cosPitch) = MathF.SinCos(pitchDegrees * (MathF.PI / 180f));
        var (sinYaw, cosYaw) = MathF.SinCos(yawDegrees * (MathF.PI / 180f));
        x = cosPitch * cosYaw;
        y = cosPitch * sinYaw;
        z = -sinPitch;
    }

    public static void GetLookAtPoint(float originX, float originY, float originZ, QAngle angles, out float x, out float y, out float z)
    {
        GetForwardVector(angles.X, angles.Y, out var forwardX, out var forwardY, out var forwardZ);
        x = originX + forwardX * LookAtDistance;
        y = originY + forwardY * LookAtDistance;
        z = originZ + forwardZ * LookAtDistance;
    }

    public Vector? CalculateForward(Vector origin, QAngle angle)
    {
        GetLookAtPoint(origin.X, origin.Y, origin.Z, angle, out var x, out var y, out var z);
        return new Vector(x, y, z);
    }

    public bool TryGetEyePosition(CBasePlayerPawn? playerPawn, out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (playerPawn == null || !playerPawn.IsValid || playerPawn.CameraServices == null)
            return false;

        var origin = playerPawn.AbsOrigin;
        if (origin == null)
            return false;

        x = origin.X;
        y = origin.Y;
        z = origin.Z + playerPawn.ViewOffset.Z;
        return true;
    }

    public Vector? GetEyePosition<T>(T? playerPawn)
        where T : CBasePlayerPawn
    {
        return TryGetEyePosition(playerPawn, out var x, out var y, out var z) ? new Vector(x, y, z) : null;
    }

    public Vector? GetFreecamPlayerPosition(CCSPlayerController? player)
    {
        if (player == null || !IsValid(player))
        {
            return null;
        }
        return player.Pawn.Value!.CBodyComponent?.SceneNode?.GetSkeletonInstance().AbsOrigin.Clone() ?? null;
    }

    // Doors are disabled for now.
    // public string GetDoorKey(Vector origin)
    // {
    //     return $"{(int)origin.X} {(int)origin.Y} {(int)origin.Z}";
    // }
}

static class VectorExtensions
{
    public static Vector Clone(this Vector vector)
    {
        return new Vector(vector.X, vector.Y, vector.Z);
    }
}

static class QAngleExtensions
{
    public static QAngle Clone(this QAngle angle)
    {
        return new QAngle(angle.X, angle.Y, angle.Z);
    }
}
