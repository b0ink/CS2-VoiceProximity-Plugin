using CounterStrikeSharp.API.Core;
using System.Text.Json.Serialization;

namespace ProximityChat
{
    public class Config : BasePluginConfig
    {
        public string? DatabaseHost { get; set; } = "localhost";
        public int DatabasePort { get; set; } = 3306;
        public string? DatabaseUser { get; set; } = "username";
        public string? DatabasePassword { get; set; } = "password";
        public string? DatabaseName { get; set; } = "database";
    }
}