using Game.Config.TableConfig;
using Luban;

namespace Game.Server.Configuration
{
    internal static class TableConfigLoader
    {
        internal static Tables Load(string? directory)
        {
            string dataDirectory = Path.GetFullPath(directory ?? Path.Combine("Config", "Bytes"), AppContext.BaseDirectory);
            return new Tables(name => new ByteBuf(File.ReadAllBytes(Path.Combine(dataDirectory, name + ".bytes"))));
        }
    }
}
