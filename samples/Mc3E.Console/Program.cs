using System.Text;

internal static class Program
{
    private static int Main(string[] args)
    {
        var started = DateTimeOffset.Now;
        var captured = new StringBuilder();
        var exitCode = 0;

        void Print(string value)
        {
            Console.WriteLine(value);
            captured.AppendLine(value);
        }

        try { Mc3EConsoleApp.Run(args, Print); }
        catch (Exception ex)
        {
            exitCode = 1;
            Print("Error: " + ex.Message);
        }
        finally
        {
            var finished = DateTimeOffset.Now;
            var directory = Path.Combine(Environment.CurrentDirectory, "output");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, started.ToString("yyyyMMdd") + "_log.txt");
            using (var writer = new StreamWriter(path, append: true, Encoding.UTF8))
            {
                writer.WriteLine(new string('=', 72));
                writer.WriteLine("Started: " + started.ToString("O"));
                writer.WriteLine("Command: " + Environment.CommandLine);
                writer.Write(captured.ToString());
                writer.WriteLine("Exit code: " + exitCode);
                writer.WriteLine("Finished: " + finished.ToString("O"));
            }
            Console.WriteLine("Log file: " + Path.GetFullPath(path));
        }
        return exitCode;
    }
}
