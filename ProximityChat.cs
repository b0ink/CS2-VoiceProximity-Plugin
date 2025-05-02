using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using Nexd.MySQL;
using MySqlConnector;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Admin;

namespace ProximityChat;


public class ProximityChat : BasePlugin, IPluginConfig<Config>
{
    public override string ModuleName => "Proximity Chat API";
    public override string ModuleAuthor => "b0ink";
    public override string ModuleVersion => typeof(ProximityChat).Assembly.GetName().Version?.ToString() ?? "n/a";

    private MySqlDb? _db;

    public Config Config { get; set; } = new();


    //TODO: relay position data of the spectator when theyre dead
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


    public Vector? GetCoordinatePlayerIsLookingAt(CCSPlayerController? player)
    {
        if(!IsValid(player)) return null;
        var origin = GetEyePosition(player!.PlayerPawn.Value!);
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
            //if (player.SteamID == 0) continue;
            // check alive state
            bool useObserverPawn = false;
            if (player.PlayerPawn.Value!.LifeState != (byte)LifeState_t.LIFE_ALIVE)
            {
                useObserverPawn = true;
                useObserverPawn = false;
                Console.WriteLine("saving observer pawn");
            }

            SavePlayerData(_db, player, useObserverPawn);
        }
    }

    public static void CreateTable(MySqlDb? db)
    {
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
                        `AngleX` float NOT NULL,
                        `AngleY` float NOT NULL,
                        `AngleZ` float NOT NULL,
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
            //pawn = player.Pawn.Value as CCSPlayerPawn;
            //var observer = player.Controller.ObserverPawn?.Value?.Controller?.Value;
            var observer = player.ObserverPawn.Value?.ObserverServices?.ObserverTarget.Value;
            if (observer != null && observer.IsValid)
            {
                if (observer.DesignerName == "player")
                {
                    pawn = observer.As<CCSPlayerController>().PlayerPawn.Value;

                }
            }
            //.Controller.Value

            if (observer != null && observer.IsValid)
            {
            }
        }
        if (pawn == null || !pawn.IsValid)
        {
            return;
        }
        //var origin = pawn.AbsOrigin;
        var origin = GetEyePosition(pawn);
        var angles = pawn.EyeAngles;
        if (origin == null || angles == null)
        {
            return;
        }
        string positionData = $"{origin.X},{origin.Y},{origin.Z},{angles.X},{angles.Y},{angles.Z}";

        var SteamId = MySqlHelper.EscapeString(player!.SteamID.ToString());
        var Name = MySqlHelper.EscapeString(player.PlayerName);

        var OriginX = origin.X.ToString();
        var OriginY = origin.Y.ToString();
        var OriginZ = origin.Z.ToString();

        var AngleX = angles.X.ToString();
        var AngleY = angles.Y.ToString();
        var AngleZ = angles.Z.ToString();

        var LookAt = GetCoordinatePlayerIsLookingAt(player);
        var LookAtX = LookAt.X.ToString();
        var LookAtY = LookAt.Y.ToString();
        var LookAtZ = LookAt.Z.ToString();


        var playerIsAlive = IsAlive(player) ? 1 : 0;
        var Team = (int)player.TeamNum;
        var tteam = player.Team;

        MySqlQueryValue values = new MySqlQueryValue()
                                .Add("SteamId", player!.SteamID.ToString())
                                .Add("Name", player.PlayerName)
                                .Add("OriginX", OriginX)
                                .Add("OriginY", OriginY)
                                .Add("OriginZ", OriginZ)
                                .Add("AngleX", AngleX)
                                .Add("AngleY", AngleY)
                                .Add("AngleZ", AngleZ)
                                .Add("LookAtX", LookAtX)
                                .Add("LookAtY", LookAtY)
                                .Add("LookAtZ", LookAtZ)
                                .Add("IsAlive", playerIsAlive.ToString())
                                .Add("Team", Team.ToString());



        try
        {

        //    var query = $@"
        //INSERT INTO `ProximityData` 
        //(`SteamId`, `Name`, `OriginX`, `OriginY`, `OriginZ`, `AngleX`, `AngleY`, `AngleZ`, `IsAlive`, `Team`)
        //VALUES
        //('{SteamId}', '{Name}', '{OriginX}', '{OriginY}', '{OriginZ}', '{AngleX}', '{AngleY}', '{AngleZ}', {playerIsAlive}, {Team})
        //ON DUPLICATE KEY UPDATE
        //`LastUpdated` = CURRENT_TIMESTAMP()";
            db!.Table("ProximityData").InsertIfNotExist(values, $@"
                `SteamId` = '{SteamId}',
                `Name` = '{Name}',
                `OriginX` = '{OriginX}',
                `OriginY` = '{OriginY}',
                `OriginZ` = '{OriginZ}',
                `AngleX` = '{AngleX}',
                `AngleY` = '{AngleY}',
                `AngleZ` = '{AngleZ}',
                `LookAtX` = '{LookAtX}',
                `LookAtY` = '{LookAtY}',
                `LookAtZ` = '{LookAtZ}',
                `IsAlive` = '{playerIsAlive}',
                `Team` = '{Team}',
                `LastUpdated` = CURRENT_TIMESTAMP()
            ");

            //db!.Table("ProximityData").InsertIfNotExistAsync(values, $"`Name` = '{player.PlayerName}', `LastUpdated` = CURRENT_TIMESTAMP(), `Data` = '{positionData}'");

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