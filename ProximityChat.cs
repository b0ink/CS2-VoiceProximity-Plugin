using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Nexd.MySQL;
using MySqlConnector;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;
using System.Reflection;

namespace ProximityChat;


public class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => PluginVersion ?? "n/a";

    private MySqlDb? _db;
    public Config Config { get; set; } = new();

    public override void Load(bool hotReload)
    {
        _db = new(Config.DatabaseHost ?? string.Empty, Config.DatabaseUser ?? string.Empty, Config.DatabasePassword ?? string.Empty, Config.DatabaseName ?? string.Empty, Config.DatabasePort);
        CreateTable(_db);
        RegisterListener<Listeners.OnMapStart>(mapName =>
        {
            AddTimer(0.1f, () =>
            {
                SaveAllPlayersPositions();
            }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE | CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        });
    }


    [ConsoleCommand("css_savepositions")]
    [RequiresPermissions("#css/admin")]
    public void Command_savepositions(CCSPlayerController? caller, CommandInfo info)
    {
        info.ReplyToCommand($"DEBUG: Saving player positions...");
        SaveAllPlayersPositions();
    }


    public CCSPlayerController? GetObserverTarget(CCSPlayerController? observer)
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
        if (observedEntity == null)
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


    public Vector? GetCoordinatePlayerIsLookingAt(CCSPlayerController? player)
    {
        if (!IsValid(player))
        {
            return null;
        }

        var origin = GetEyePosition(player!.PlayerPawn.Value!);
        if(origin == null)
        {
            return null;
        }

        var angle = player.PlayerPawn.Value!.EyeAngles;

        Vector _forward = new();
        NativeAPI.AngleVectors(angle.Handle, _forward.Handle, 0, 0);
        Vector _endOrigin = new(origin.X + _forward.X * 8192, origin.Y + _forward.Y * 8192, origin.Z + _forward.Z * 8192);

        return _endOrigin;
    }

    public void SaveAllPlayersPositions()
    {
        foreach (var player in Utilities.GetPlayers().Where(IsValid))
        {
            //if (player.SteamID == 0) continue; // ignore bots

            bool useObserverPawn = false;
            if (player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                useObserverPawn = true;
            }

            SavePlayerData(_db, player, useObserverPawn);
        }
    }

    public static void CreateTable(MySqlDb? db)
    {
        if (db == null)
        {
            Console.WriteLine("Error: database is not initialised");
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                Console.WriteLine("Dropping existing ProximityData table...");
                await db.ExecuteNonQueryAsync("DROP TABLE IF EXISTS `ProximityData`;");

                Console.WriteLine("Creating table...");
                int result = await db.ExecuteNonQueryAsync(@"
                    CREATE TABLE `ProximityData` (
                        `Id` int(11) NOT NULL AUTO_INCREMENT,
                        `SteamId` varchar(18) NOT NULL,
                        `Name` varchar(128) NOT NULL,
                        `OriginX` float NOT NULL,
                        `OriginY` float NOT NULL,
                        `OriginZ` float NOT NULL,
                        `LookAtX` float NOT NULL,
                        `LookAtY` float NOT NULL,
                        `LookAtZ` float NOT NULL,
                        `IsAlive` TINYINT(1) NOT NULL,
                        `Team` int NOT NULL,
                        `LastUpdated` timestamp NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
                        PRIMARY KEY (`Id`),
                        UNIQUE KEY `SteamId` (`SteamId`)
                    );
                ");

                if (result != 0)
                {
                    Console.WriteLine("Table creation completed successfully.");
                }
                else
                {
                    Console.WriteLine("Warning: table creation query returned 0.");
                }
            }
            catch (Exception ex)
            {
                Console.BackgroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error during table creation: {ex}");
                Console.ResetColor();
            }
        });
    }


    public void SavePlayerData(MySqlDb? db, CCSPlayerController? player, bool useObserverPawn)
    {
        if (db == null)
        {
            return;
        }

        if (!IsValid(player))
        {
            return;
        }

        var pawn = player!.PlayerPawn.Value;
        if (useObserverPawn)
        {
            // This is only effective if cameras are forced for first person
            // TODO: find another method to get positions of players in freecam
            var observingTarget = GetObserverTarget(player);
            if (observingTarget != null && IsValid(observingTarget))
            {
                pawn = observingTarget.PlayerPawn.Value;
            }
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        var origin = GetEyePosition(pawn);
        var angles = pawn.EyeAngles;
        if (origin == null || angles == null)
        {
            return;
        }

        var SteamId = MySqlHelper.EscapeString(player!.SteamID.ToString());
        var Name = MySqlHelper.EscapeString(player.PlayerName);

        var OriginX = origin.X.ToString();
        var OriginY = origin.Y.ToString();
        var OriginZ = origin.Z.ToString();

        var LookAt = GetCoordinatePlayerIsLookingAt(player)!;
        var LookAtX = LookAt.X.ToString();
        var LookAtY = LookAt.Y.ToString();
        var LookAtZ = LookAt.Z.ToString();


        var playerIsAlive = IsAlive(player) ? 1 : 0;
        var Team = (int)player.TeamNum;

        var values =
        new MySqlQueryValue()
            .Add("SteamId", player!.SteamID.ToString())
            .Add("Name", player.PlayerName)
            .Add("OriginX", OriginX)
            .Add("OriginY", OriginY)
            .Add("OriginZ", OriginZ)
            .Add("LookAtX", LookAtX)
            .Add("LookAtY", LookAtY)
            .Add("LookAtZ", LookAtZ)
            .Add("IsAlive", playerIsAlive.ToString())
            .Add("Team", Team.ToString());

        try
        {
            db!.Table("ProximityData").InsertIfNotExist(values, $@"
                `SteamId` = '{SteamId}',
                `Name` = '{Name}',
                `OriginX` = '{OriginX}',
                `OriginY` = '{OriginY}',
                `OriginZ` = '{OriginZ}',
                `LookAtX` = '{LookAtX}',
                `LookAtY` = '{LookAtY}',
                `LookAtZ` = '{LookAtZ}',
                `IsAlive` = '{playerIsAlive}',
                `Team` = '{Team}',
                `LastUpdated` = CURRENT_TIMESTAMP()
            ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting player position: {ex.Message}");
        }

    }

    public bool IsValid(CCSPlayerController? playerController)
    {
        if (playerController == null) return false;
        if (playerController.IsValid == false) return false;
        if (playerController.IsHLTV) return false;
        if (playerController.Connected != PlayerConnectedState.PlayerConnected) return false;
        if (playerController.PlayerPawn?.Value == null) return false;
        if (playerController.PlayerPawn.IsValid == false) return false;

        return true;
    }

    public bool IsAlive(CCSPlayerController? player)
    {
        if (player != null && IsValid(player))
        {
            if (player!.PlayerPawn.Value!.LifeState == (byte)LifeState_t.LIFE_ALIVE)
            {
                return true;
            }
        }
        return false;
    }


    /// <summary>
    /// Gets the eye position of the player pawn in world coordinates.
    /// </summary>
    /// <param name="playerPawn">The player pawn to get the eye position from.</param>
    /// <returns>A <see cref="Vector"/> representing the eye position, or null if the position couldn't be determined.</returns>
    public Vector? GetEyePosition(CCSPlayerPawn playerPawn)
    {
        return playerPawn.AbsOrigin is not { } absOrigin || playerPawn.CameraServices is not { } cameraServices
            ? null
            : new Vector(absOrigin.X, absOrigin.Y, absOrigin.Z + cameraServices.OldPlayerViewOffsetZ);
    }

    public override void Unload(bool hotReload)
    {

    }

    public void OnConfigParsed(Config config)
    {
        this.Config = config;
    }

    public AssemblyName assemblyName = typeof(ProximityChat).Assembly.GetName();
    public string? AssemblyVersion => assemblyName.Version?.ToString(); // Version: x.x.x.x
    public string? PluginVersion => AssemblyVersion?.Remove(AssemblyVersion.Length - 2); // truncate to x.x.x
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