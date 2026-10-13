namespace GameFrame.ConfigTools
{
    public static class Program
    {
        public static async Task<int> Main(string[] args)
        {
            GeneratorOptions options;
            try
            {
                options = GeneratorOptions.Parse(args);
            }
            catch (ArgumentException exception)
            {
                Console.Error.WriteLine(exception.Message);
                return 2;
            }

            using var cancellation = new CancellationTokenSource();
            ConsoleCancelEventHandler cancel = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                cancellation.Cancel();
            };
            Console.CancelKeyPress += cancel;
            try
            {
                string root = LubanGenerator.FindRoot(options.Root, AppContext.BaseDirectory);
                return await LubanGenerator.RunAsync(root, options, LubanGenerator.FindDotNet(),
                    Console.Out, Console.Error, cancellation.Token);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                Console.Error.WriteLine("Configuration generation canceled.");
                return 130;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancel;
            }
        }
    }
}
