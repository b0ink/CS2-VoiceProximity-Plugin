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
        AddTimer(0.1f, () =>
        {
            SaveAllPlayersPositions();
        }, CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE | CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
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




    public void SaveAllPlayersPositions()
    {
        foreach (var player in Utilities.GetPlayers().Where(IsValid))
        {
            //if (player.SteamID == 0) continue; // ignore bots

            bool useObserverPawn = false;
            if (player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                // Keep the camera position in the same spot for a few seconds before teleporting to the player they're spectating
                float timeSinceDeath = Server.CurrentTime - player.PlayerPawn.Value.DeathTime;
                if (timeSinceDeath >= 3)
                {
                    useObserverPawn = true;
                }

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

        string OriginX = "";
        string OriginY = "";
        string OriginZ = "";

        string LookAtX = "";
        string LookAtY = "";
        string LookAtZ = "";


        var SteamId = MySqlHelper.EscapeString(player!.SteamID.ToString());
        var Name = MySqlHelper.EscapeString(player.PlayerName);

        CBasePlayerPawn? pawn = player!.PlayerPawn.Value;
        bool gotOriginAndAngles = false;
        if (useObserverPawn)
        {
            // This is only effective if cameras are forced for first person
            // TODO: find another method to get positions of players in freecam
            var observingTarget = GetObserverTarget(player);
            if (observingTarget != null && IsValid(observingTarget))
            {
                pawn = observingTarget.Pawn.Value;
            }

            if (GetObserverEntity(player)?.DesignerName == "planted_c4")
            {

                var vAngle = player.Pawn.Value!.V_angle.Clone();
                var c4Position = player.Pawn.Value!.AbsOrigin?.Clone();

                // Calculate third person position when spectating planted c4
                Vector forward = new();
                NativeAPI.AngleVectors(vAngle.Handle, forward.Handle, 0, 0);

                // TODO: Could we TraceRay to the c4 to detect if cameraPos is inside an object, and reduce camDistance until the trace is clear?
                const float camDistance = 100;
                float lowestZ = c4Position.Z + 25;

                Vector offset = new(-forward.X * camDistance, -forward.Y * camDistance, -forward.Z * camDistance);
                Vector cameraPos = new(c4Position.X + offset.X, c4Position.Y + offset.Y, c4Position.Z + offset.Z);

                OriginX = $"{cameraPos.X}";
                OriginY = $"{cameraPos.Y}";
                OriginZ = $"{(cameraPos.Z < lowestZ ? lowestZ : cameraPos.Z)}"; // Prevent the camera from going under the floor

                LookAtX = $"{c4Position.X}";
                LookAtY = $"{c4Position.Y}";
                LookAtZ = $"{c4Position.Z}";

                gotOriginAndAngles = true;
            }
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        if (!gotOriginAndAngles)
        {
            var origin = GetEyePosition(pawn)?.Clone();
            var angles = pawn.V_angle.Clone();
            if (origin == null || angles == null)
            {
                return;
            }

            OriginX = origin.X.ToString();
            OriginY = origin.Y.ToString();
            OriginZ = origin.Z.ToString();

            var LookAt = CalculateForward(origin, angles)!;
            LookAtX = LookAt.X.ToString();
            LookAtY = LookAt.Y.ToString();
            LookAtZ = LookAt.Z.ToString();
        }





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

    public Vector? GetEyePosition<T>(T? playerPawn) where T : CBasePlayerPawn
    {
        if (playerPawn == null || !playerPawn.IsValid || playerPawn.CameraServices == null || playerPawn.AbsOrigin == null)
            return null;

        var absOrigin = playerPawn.AbsOrigin.Clone();
        var cameraServices = playerPawn.CameraServices;
        return new Vector(absOrigin.X, absOrigin.Y, absOrigin.Z + cameraServices.OldPlayerViewOffsetZ);
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