namespace GameFrame.ProtocolTools
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if ((args.Length != 1 && args.Length != 2) ||
                    (args[0] != "generate" && args[0] != "check") ||
                    (args.Length == 2 && args[1] != "--artifacts"))
                {
                    Console.Error.WriteLine("Usage: dotnet run --project NetworkProtocol -- generate|check [--artifacts]");
                    return 2;
                }

                string root = FindRoot();
                IReadOnlyDictionary<string, byte[]> outputs = ProtoProtocolGenerator.Generate(Path.Combine(root, "Protocol"), args.Length == 2);
                int result = WriteOutputs(root, outputs, args[0] == "check");
                if (result == 0)
                {
                    Console.WriteLine($"Protocol {args[0]} succeeded ({outputs.Count} files): {root}");
                }

                return result;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 1;
            }
        }

        internal static int WriteOutputs(string root, IReadOnlyDictionary<string, byte[]> outputs, bool checkOnly)
        {
            bool mismatch = false;
            foreach (var output in outputs)
            {
                string path = Path.Combine(root, output.Key);
                bool same = File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(output.Value);
                if (same)
                {
                    continue;
                }

                if (checkOnly)
                {
                    Console.Error.WriteLine($"Generated protocol differs: {output.Key}");
                    mismatch = true;
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllBytes(path, output.Value);
                }
            }

            string generated = Path.Combine(root, "Unity/Assets/Scripts/Hotfix/Network/Generated");
            if (Directory.Exists(generated))
            {
                foreach (string path in Directory.EnumerateFiles(generated, "*.cs"))
                {
                    string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                    if (!outputs.ContainsKey(relative))
                    {
                        Console.Error.WriteLine($"Unexpected C# file in generated directory: {relative}. Remove it with its Unity metadata.");
                        mismatch = true;
                    }
                }
            }

            return mismatch ? 1 : 0;
        }

        private static string FindRoot()
        {
            for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Protocol", "GameFrame.Protocol.projitems")) &&
                    File.Exists(Path.Combine(directory.FullName, "DotNet", "DotNet.sln")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("Could not locate the GameFrame repository.");
        }
    }
}
