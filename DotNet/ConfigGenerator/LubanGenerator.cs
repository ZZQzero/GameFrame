using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameFrame.ConfigTools
{
    internal static class LubanGenerator
    {
        private sealed record TargetOutput(string Name, string CodeDirectory, string DataDirectory);

        internal static string FindRoot(string? explicitRoot, string startDirectory)
        {
            if (explicitRoot != null)
            {
                if (!IsRepository(explicitRoot))
                {
                    throw new DirectoryNotFoundException($"Not a GameFrame repository: {explicitRoot}");
                }

                return explicitRoot;
            }

            for (DirectoryInfo? directory = new DirectoryInfo(startDirectory); directory != null; directory = directory.Parent)
            {
                if (IsRepository(directory.FullName))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Could not locate the GameFrame repository. Use --root to specify it.");
        }

        private static bool IsRepository(string root) =>
            File.Exists(Path.Combine(root, "DotNet", "DotNet.sln")) &&
            File.Exists(Path.Combine(root, "Unity", "Config", "luban.conf"));

        internal static string FindDotNet()
        {
            string? configured = Environment.GetEnvironmentVariable("DOTNET_EXECUTABLE") ??
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            string executable = configured ?? Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),
                "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable))
            {
                throw new FileNotFoundException("Could not locate dotnet. Set DOTNET_EXECUTABLE to its absolute path.", executable);
            }

            return executable;
        }

        internal static async Task<int> RunAsync(string root, GeneratorOptions options, string dotnet,
            TextWriter output, TextWriter error, CancellationToken cancellation)
        {
            string configRoot = Path.Combine(root, "Unity", "Config");
            string luban = Path.Combine(configRoot, "Luban", "Luban.dll");
            if (!File.Exists(luban))
            {
                throw new FileNotFoundException("The pinned Luban generator is missing.", luban);
            }

            var targets = new List<TargetOutput>();
            if (options.Target == "client" || options.Target == "all")
            {
                targets.Add(new TargetOutput("client",
                    OutputDirectory("LUBAN_OUTPUT_CODE_DIR", "../Assets/Scripts/Hotfix/TableConfig/Generated", configRoot),
                    OutputDirectory("LUBAN_OUTPUT_DATA_DIR", "../Assets/Config/Excel/Bytes", configRoot)));
            }

            if (options.Target == "server" || options.Target == "all")
            {
                targets.Add(new TargetOutput("server",
                    OutputDirectory("LUBAN_SERVER_OUTPUT_CODE_DIR", "../../DotNet/Game.Config/Generated", configRoot),
                    OutputDirectory("LUBAN_SERVER_OUTPUT_DATA_DIR", "../../DotNet/Game.Config/Bytes", configRoot)));
            }

            string? temporary = options.CheckOnly
                ? Directory.CreateTempSubdirectory("gameframe-config-check-").FullName
                : null;
            try
            {
                bool matches = true;
                foreach (TargetOutput target in targets)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string codeDirectory = temporary == null ? target.CodeDirectory : Path.Combine(temporary, target.Name, "Code");
                    string dataDirectory = temporary == null ? target.DataDirectory : Path.Combine(temporary, target.Name, "Bytes");
                    var start = new ProcessStartInfo(dotnet)
                    {
                        WorkingDirectory = configRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    foreach (string argument in new[]
                    {
                        luban, "-t", target.Name, "-c", "cs-bin", "-d", "bin", "--strict",
                        "--conf", Path.Combine(configRoot, "luban.conf"),
                        "-x", "outputCodeDir=" + codeDirectory, "-x", "outputDataDir=" + dataDirectory
                    })
                    {
                        start.ArgumentList.Add(argument);
                    }

                    await output.WriteLineAsync($"Luban {target.Name}: {(options.CheckOnly ? "check" : "generate")}");
                    int exitCode = await RunProcessAsync(start, output, error, cancellation);
                    if (exitCode != 0)
                    {
                        await error.WriteLineAsync($"Luban {target.Name} failed (ExitCode={exitCode}).");
                        return exitCode;
                    }

                    if (options.CheckOnly)
                    {
                        matches &= CompareDirectory(codeDirectory, target.CodeDirectory, error);
                        matches &= CompareDirectory(dataDirectory, target.DataDirectory, error);
                    }
                }

                if (!matches)
                {
                    return 1;
                }

                await output.WriteLineAsync($"Configuration {(options.CheckOnly ? "check" : "generation")} succeeded ({options.Target}): {root}");
                return 0;
            }
            finally
            {
                if (temporary != null)
                {
                    Directory.Delete(temporary, true);
                }
            }
        }

        private static string OutputDirectory(string variable, string defaultPath, string configRoot)
        {
            string? value = Environment.GetEnvironmentVariable(variable);
            return Path.GetFullPath(string.IsNullOrEmpty(value) ? defaultPath : value, configRoot);
        }

        private static async Task<int> RunProcessAsync(ProcessStartInfo start, TextWriter output, TextWriter error,
            CancellationToken cancellation)
        {
            using var process = new Process { StartInfo = start };
            cancellation.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                throw new InvalidOperationException($"Could not start Luban: {start.FileName}");
            }

            Task stdout = ForwardAsync(process.StandardOutput, output);
            Task stderr = ForwardAsync(process.StandardError, error);
            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }

                await process.WaitForExitAsync(CancellationToken.None);
                await Task.WhenAll(stdout, stderr);
                throw;
            }

            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }

        private static async Task ForwardAsync(StreamReader reader, TextWriter writer)
        {
            while (await reader.ReadLineAsync() is string line)
            {
                await writer.WriteLineAsync(line);
            }
        }

        internal static bool CompareDirectory(string expectedDirectory, string actualDirectory, TextWriter error)
        {
            Dictionary<string, string> expected = Files(expectedDirectory);
            Dictionary<string, string> actual = Files(actualDirectory);
            bool matches = true;
            foreach (string name in expected.Keys.Union(actual.Keys).Order(StringComparer.Ordinal))
            {
                if (!expected.TryGetValue(name, out string? expectedPath))
                {
                    error.WriteLine($"Unexpected generated file: {Path.Combine(actualDirectory, name)}");
                    matches = false;
                }
                else if (!actual.TryGetValue(name, out string? actualPath))
                {
                    error.WriteLine($"Missing generated file: {Path.Combine(actualDirectory, name)}");
                    matches = false;
                }
                else if (!File.ReadAllBytes(expectedPath).AsSpan().SequenceEqual(File.ReadAllBytes(actualPath)))
                {
                    error.WriteLine($"Generated file differs: {actualPath}");
                    matches = false;
                }
            }

            return matches;
        }

        private static Dictionary<string, string> Files(string directory)
        {
            return Directory.Exists(directory)
                ? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => Path.GetExtension(path) != ".meta" && Path.GetExtension(path) != ".uid")
                    .ToDictionary(path => Path.GetRelativePath(directory, path), path => path, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
