namespace GameFrame.ConfigTools
{
    internal sealed record GeneratorOptions(bool CheckOnly, string Target, string? Root)
    {
        internal const string Usage = "Usage: ConfigGenerator generate|check [--target client|server|all] [--root repository-path]";

        internal static GeneratorOptions Parse(string[] args)
        {
            if (args.Length == 0 || (args[0] != "generate" && args[0] != "check"))
            {
                throw new ArgumentException(Usage);
            }

            string target = "all";
            string? root = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !seen.Add(args[i]))
                {
                    throw new ArgumentException(Usage);
                }

                switch (args[i])
                {
                    case "--target":
                        target = args[i + 1];
                        break;
                    case "--root":
                        root = Path.GetFullPath(args[i + 1]);
                        break;
                    default:
                        throw new ArgumentException(Usage);
                }
            }

            if (target != "client" && target != "server" && target != "all")
            {
                throw new ArgumentException(Usage);
            }

            return new GeneratorOptions(args[0] == "check", target, root);
        }
    }
}
