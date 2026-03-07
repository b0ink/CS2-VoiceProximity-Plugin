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
        if (playerController.Connected != PlayerConnectedState.PlayerConnected)
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

    public Vector? CalculateForward(Vector origin, QAngle angle)
    {
        Vector _forward = new();
        NativeAPI.AngleVectors(angle.Handle, _forward.Handle, 0, 0);
        Vector _endOrigin = new(origin.X + _forward.X * 8192, origin.Y + _forward.Y * 8192, origin.Z + _forward.Z * 8192);
        return _endOrigin;
    }

    public Vector? GetEyePosition<T>(T? playerPawn)
        where T : CBasePlayerPawn
    {
        if (playerPawn == null || !playerPawn.IsValid || playerPawn.CameraServices == null || playerPawn.AbsOrigin == null)
            return null;

        var origin = playerPawn.AbsOrigin.Clone();
        var viewOffsetZ = playerPawn.ViewOffset.Z;

        return new Vector(origin.X, origin.Y, origin.Z + viewOffsetZ);
    }

    public Vector? GetFreecamPlayerPosition(CCSPlayerController? player)
    {
        if (player == null || !IsValid(player))
        {
            return null;
        }
        return player.Pawn.Value!.CBodyComponent?.SceneNode?.GetSkeletonInstance().AbsOrigin.Clone() ?? null;
    }

    public string GetDoorKey(Vector origin)
    {
        return $"{(int)origin.X} {(int)origin.Y} {(int)origin.Z}";
    }
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
