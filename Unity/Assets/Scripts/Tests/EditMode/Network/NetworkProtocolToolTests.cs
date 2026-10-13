using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using GameFrame.Editor.Network;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public class NetworkProtocolToolTests
    {
#if UNITY_EDITOR_OSX || UNITY_EDITOR_LINUX
        [TestCase(false)]
        [TestCase(true)]
        public async Task ProcessRunsInDotNetDirectoryWithRequestedModeAndDrainsBothPipes(bool checkOnly)
        {
            using var fixture = new ProcessFixture(0);
            string output = await NetworkProtocolTool.RunGeneratorAsync(fixture.Root, checkOnly, fixture.Executable);
            Assert.IsTrue(output.Contains("cwd-is-dotnet"));
            Assert.IsTrue(output.Contains("--no-launch-profile -- " + (checkOnly ? "check" : "generate") + " --artifacts"));
            Assert.IsTrue(output.Contains("stdout-end"));
            Assert.IsTrue(output.Contains("stderr-end"));
            Assert.Greater(output.Length, 262144, "两条输出管道都超过常见管道容量，验证异步排空不会互锁。");
        }

        [Test]
        public async Task NonzeroExitReportsModeExitCodeAndBothPipes()
        {
            using var fixture = new ProcessFixture(7);
            try
            {
                await NetworkProtocolTool.RunGeneratorAsync(fixture.Root, true, fixture.Executable);
                Assert.Fail("检查失败必须抛出异常。");
            }
            catch (InvalidOperationException exception)
            {
                Assert.IsTrue(exception.Message.Contains("检查失败"));
                Assert.IsTrue(exception.Message.Contains("ExitCode=7"));
                Assert.IsTrue(exception.Message.Contains("stdout-end"));
                Assert.IsTrue(exception.Message.Contains("stderr-end"));
            }
        }

        private sealed class ProcessFixture : IDisposable
        {
            public string Root { get; } = Path.Combine(Path.GetTempPath(), "gameframe protocol " + Guid.NewGuid().ToString("N"));
            public string Executable => Path.Combine(Root, "fake dotnet");

            public ProcessFixture(int exitCode)
            {
                string projectDirectory = Path.Combine(Root, "DotNet", "NetworkProtocol");
                Directory.CreateDirectory(projectDirectory);
                File.WriteAllText(Path.Combine(projectDirectory, "NetworkProtocol.csproj"), "<Project />");
                File.WriteAllText(Executable,
                    "#!/bin/sh\n[ -f NetworkProtocol/NetworkProtocol.csproj ] || exit 99\n" +
                    "echo cwd-is-dotnet\necho \"args:$*\"\n" +
                    "i=0\nwhile [ \"$i\" -lt 4096 ]; do\n" +
                    "echo stdout-0123456789012345678901234567890123456789\n" +
                    "echo stderr-0123456789012345678901234567890123456789 >&2\n" +
                    "i=$((i + 1))\ndone\necho stdout-end\necho stderr-end >&2\nexit " + exitCode + "\n");
                using var chmod = Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/chmod",
                    Arguments = "+x \"" + Executable + "\"",
                    UseShellExecute = false
                });
                chmod.WaitForExit();
                Assert.AreEqual(0, chmod.ExitCode);
            }

            public void Dispose()
            {
                Directory.Delete(Root, true);
            }
        }
#endif
    }
}
