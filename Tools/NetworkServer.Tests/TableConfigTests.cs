using Game.Server.Configuration;
using Luban;
using NUnit.Framework;

namespace GameFrame.Tests
{
    [TestFixture]
    public sealed class TableConfigTests
    {
        [TestCase(null)]
        [TestCase("Config/Bytes")]
        public void LoadsDeployedServerTables(string? directory)
        {
            var tables = TableConfigLoader.Load(directory);
            Assert.That(tables.TbAIRobot.DataList, Is.Not.Empty);
            Assert.That(tables.TbGlobalConst.Data, Is.Not.Null);
            Assert.That(tables.GetType().GetProperty("TbLanguage"), Is.Null);
            Assert.That(tables.GetType().GetProperty("TbGameScene"), Is.Null);
        }

        [Test]
        public void MissingDirectoryStopsLoading()
        {
            string directory = Path.Combine(Path.GetTempPath(), "gameframe-missing-config-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<DirectoryNotFoundException>(() => TableConfigLoader.Load(directory));
        }

        [Test]
        public void MissingRequiredTableStopsLoading()
        {
            WithTemporaryData(directory =>
            {
                File.Delete(Path.Combine(directory, "tbairobot.bytes"));
                Assert.Throws<FileNotFoundException>(() => TableConfigLoader.Load(directory));
            });
        }

        [Test]
        public void InvalidSingletonTableStopsLoading()
        {
            WithTemporaryData(directory =>
            {
                File.WriteAllBytes(Path.Combine(directory, "tbglobalconst.bytes"), new byte[] { 0 });
                Assert.Throws<SerializationException>(() => TableConfigLoader.Load(directory));
            });
        }

        private static void WithTemporaryData(Action<string> action)
        {
            string directory = Path.Combine(Path.GetTempPath(), "gameframe-config-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                foreach (string file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Config", "Bytes"), "*.bytes"))
                {
                    File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
                }

                action(directory);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
