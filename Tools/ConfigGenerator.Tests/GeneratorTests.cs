using System.Diagnostics;
using GameFrame.ConfigTools;
using NUnit.Framework;

namespace GameFrame.ConfigToolTests
{
    [TestFixture]
    [NonParallelizable]
    public sealed class GeneratorTests
    {
        [TestCase("generate", false)]
        [TestCase("check", true)]
        public void DefaultTargetIncludesBothEnds(string command, bool checkOnly)
        {
            GeneratorOptions options = GeneratorOptions.Parse(new[] { command });
            Assert.That(options.CheckOnly, Is.EqualTo(checkOnly));
            Assert.That(options.Target, Is.EqualTo("all"));
        }

        [TestCase("")]
        [TestCase("generate --target")]
        [TestCase("generate --target all --target server")]
        [TestCase("generate --target editor")]
        [TestCase("generate --unknown all")]
        [TestCase("build")]
        public void InvalidArgumentsAreRejected(string arguments)
        {
            Assert.Throws<ArgumentException>(() => GeneratorOptions.Parse(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)));
        }

        [Test]
        public void RootDiscoveryUsesToolLocationAndHonorsExplicitFailure()
        {
            using var fixture = new Fixture();
            string nested = Path.Combine(fixture.Root, "DotNet", "ConfigGenerator", "bin");
            Assert.That(LubanGenerator.FindRoot(null, nested), Is.EqualTo(fixture.Root));
            Assert.Throws<DirectoryNotFoundException>(() => LubanGenerator.FindRoot(Path.Combine(fixture.Root, "missing"), nested));
        }

        [Test]
        public void ComparisonReportsMissingExtraAndChangedFilesWithoutWritingAssets()
        {
            using var fixture = new Fixture();
            string expected = Path.Combine(fixture.Root, "expected");
            string actual = Path.Combine(fixture.Root, "actual");
            Directory.CreateDirectory(expected);
            Directory.CreateDirectory(actual);
            File.WriteAllText(Path.Combine(expected, "changed.cs"), "new");
            File.WriteAllText(Path.Combine(actual, "changed.cs"), "old");
            File.WriteAllText(Path.Combine(expected, "missing.bytes"), "missing");
            File.WriteAllText(Path.Combine(actual, "extra.cs"), "extra");
            File.WriteAllText(Path.Combine(actual, "changed.cs.meta"), "guid: untouched");
            var before = Snapshot(actual);
            using var error = new StringWriter();

            Assert.That(LubanGenerator.CompareDirectory(expected, actual, error), Is.False);
            Assert.That(error.ToString(), Does.Contain("Missing generated file").And.Contain("Unexpected generated file").And.Contain("Generated file differs"));
            Assert.That(error.ToString(), Does.Not.Contain(".meta"));
            Assert.That(Snapshot(actual), Is.EquivalentTo(before));
        }

        [Test]
        public async Task ServerGenerationDoesNotTouchClientOutputsAndDrainsBothPipes()
        {
            using var fixture = new Fixture(withProcess: true);
            using var output = new StringWriter();
            using var error = new StringWriter();
            int result = await LubanGenerator.RunAsync(fixture.Root, new GeneratorOptions(false, "server", null),
                fixture.Executable, output, error, CancellationToken.None);

            Assert.That(result, Is.Zero);
            Assert.That(File.Exists(Path.Combine(fixture.Root, "DotNet/Game.Config/Generated/Tables.cs")), Is.True);
            Assert.That(Directory.Exists(Path.Combine(fixture.Root, "Unity/Assets")), Is.False);
            Assert.That(File.ReadAllLines(fixture.CallLog), Has.Length.EqualTo(1));
            Assert.That(File.ReadAllLines(fixture.CallLog)[0], Does.StartWith("server|"));
            Assert.That(output.ToString(), Does.Contain("cwd-is-config").And.Contain("stdout-end"));
            Assert.That(error.ToString(), Does.Contain("stderr-end"));
            Assert.That(output.ToString().Length, Is.GreaterThan(262144));
            Assert.That(error.ToString().Length, Is.GreaterThan(262144));
        }

        [Test]
        public async Task CheckUsesTemporaryOutputsAndLeavesGeneratedAssetsUnchanged()
        {
            using var fixture = new Fixture(withProcess: true);
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.That(await LubanGenerator.RunAsync(fixture.Root, new GeneratorOptions(false, "all", null),
                fixture.Executable, output, error, CancellationToken.None), Is.Zero);
            string assets = Path.Combine(fixture.Root, "Unity/Assets");
            File.WriteAllText(Path.Combine(assets, "Scripts/Hotfix/TableConfig/Generated/Tables.cs.meta"), "guid: keep");
            var before = Snapshot(assets);

            Assert.That(await LubanGenerator.RunAsync(fixture.Root, new GeneratorOptions(true, "all", null),
                fixture.Executable, output, error, CancellationToken.None), Is.Zero);
            Assert.That(Snapshot(assets), Is.EquivalentTo(before));
            Assert.That(File.ReadAllLines(fixture.CallLog).Select(line => line.Split('|')[0]),
                Is.EqualTo(new[] { "client", "server", "client", "server" }));
            foreach (string line in File.ReadAllLines(fixture.CallLog).Skip(2))
            {
                Assert.That(Directory.Exists(line.Split('|')[1]), Is.False, "Check must clean its temporary output.");
            }
        }

        [TestCase("client", 7, 1)]
        [TestCase("server", 9, 2)]
        public async Task FailedGenerationPreservesExitCodeAndStopsFollowingTargets(string failedTarget, int expectedExit, int calls)
        {
            using var fixture = new Fixture(withProcess: true);
            File.WriteAllText(Path.Combine(fixture.Root, "Unity/Config", "fail-" + failedTarget), "");
            using var output = new StringWriter();
            using var error = new StringWriter();
            int result = await LubanGenerator.RunAsync(fixture.Root, new GeneratorOptions(true, "all", null),
                fixture.Executable, output, error, CancellationToken.None);

            Assert.That(result, Is.EqualTo(expectedExit));
            Assert.That(File.ReadAllLines(fixture.CallLog), Has.Length.EqualTo(calls));
            Assert.That(error.ToString(), Does.Contain($"ExitCode={expectedExit}"));
            foreach (string line in File.ReadAllLines(fixture.CallLog))
            {
                Assert.That(Directory.Exists(line.Split('|')[1]), Is.False);
            }
        }

        [Test]
        public async Task CancellationJoinsChildAndCleansCheckOutput()
        {
            using var fixture = new Fixture(withProcess: true);
            File.WriteAllText(Path.Combine(fixture.Root, "Unity/Config/slow"), "");
            using var output = new StringWriter();
            using var error = new StringWriter();
            using var cancellation = new CancellationTokenSource();
            Task<int> running = LubanGenerator.RunAsync(fixture.Root, new GeneratorOptions(true, "server", null),
                fixture.Executable, output, error, cancellation.Token);
            string pidFile = Path.Combine(fixture.Root, "Unity/Config/pid");
            using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!File.Exists(pidFile))
            {
                await Task.Delay(10, guard.Token);
            }

            int pid = int.Parse(File.ReadAllText(pidFile));
            using Process process = Process.GetProcessById(pid);
            cancellation.Cancel();
            Assert.ThrowsAsync(Is.InstanceOf<OperationCanceledException>(), async () => await running);
            Assert.That(process.HasExited, Is.True);
            Assert.That(Directory.Exists(File.ReadAllLines(fixture.CallLog)[0].Split('|')[1]), Is.False);
        }

        private static string[] Snapshot(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path => path + ":" + Convert.ToHexString(File.ReadAllBytes(path)) + ":" + File.GetLastWriteTimeUtc(path).Ticks)
            .ToArray();

        private sealed class Fixture : IDisposable
        {
            internal string Root { get; } = Path.Combine(Path.GetTempPath(), "gameframe config " + Guid.NewGuid().ToString("N"));
            internal string Executable => Path.Combine(Root, "fake dotnet");
            internal string CallLog => Path.Combine(Root, "Unity/Config/calls");
            private readonly Dictionary<string, string?> environment = new();

            internal Fixture(bool withProcess = false)
            {
                if (withProcess && OperatingSystem.IsWindows())
                {
                    Assert.Ignore("The process fixture uses Bash; comparison and CLI tests are platform independent.");
                }

                foreach (string name in new[] { "LUBAN_OUTPUT_CODE_DIR", "LUBAN_OUTPUT_DATA_DIR", "LUBAN_SERVER_OUTPUT_CODE_DIR", "LUBAN_SERVER_OUTPUT_DATA_DIR" })
                {
                    environment[name] = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable(name, null);
                }

                Directory.CreateDirectory(Path.Combine(Root, "DotNet"));
                Directory.CreateDirectory(Path.Combine(Root, "Unity/Config/Luban"));
                File.WriteAllText(Path.Combine(Root, "DotNet/DotNet.sln"), "");
                File.WriteAllText(Path.Combine(Root, "Unity/Config/luban.conf"), "{}");
                File.WriteAllText(Path.Combine(Root, "Unity/Config/Luban/Luban.dll"), "");
                if (withProcess)
                {
                    File.WriteAllText(Executable, """
                        #!/bin/bash
                        set -eu
                        [ -f luban.conf ] || exit 99
                        echo cwd-is-config
                        target=
                        code=
                        data=
                        for ((i=1; i<=$#; i++)); do
                            arg="${!i}"
                            case "$arg" in
                                -t) j=$((i+1)); target="${!j}";;
                                outputCodeDir=*) code="${arg#outputCodeDir=}";;
                                outputDataDir=*) data="${arg#outputDataDir=}";;
                            esac
                        done
                        printf '%s|%s|%s\n' "$target" "$code" "$data" >> calls
                        if [ -f slow ]; then
                            echo "$$" > pid
                            sleep 30
                        fi
                        for ((i=0; i<4096; i++)); do
                            echo stdout-0123456789012345678901234567890123456789012345678901234567890123456789
                            echo stderr-0123456789012345678901234567890123456789012345678901234567890123456789 >&2
                        done
                        echo stdout-end
                        echo stderr-end >&2
                        [ ! -f fail-client ] || [ "$target" != client ] || exit 7
                        [ ! -f fail-server ] || [ "$target" != server ] || exit 9
                        mkdir -p "$code" "$data"
                        printf '%s\n' "$target" > "$code/Tables.cs"
                        printf '%s\n' "$target" > "$data/table.bytes"
                        """ + "\n");
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(Executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    }
                }
            }

            public void Dispose()
            {
                foreach (var pair in environment)
                {
                    Environment.SetEnvironmentVariable(pair.Key, pair.Value);
                }

                Directory.Delete(Root, true);
            }
        }
    }
}
