using System;
using System.IO;
using KursovaKP08.Bench;

namespace KursovaKP08;

public static class Program
{
    public static int Main(string[] args)
    {
        string profile = "small";
        int studentNumber = 1;
        string repoRoot = FindRepoRoot();

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Length)
                profile = args[++i];
            else if (args[i] == "--student-number" && i + 1 < args.Length && int.TryParse(args[++i], out int sn))
                studentNumber = sn;
            else if (args[i] == "--repo-root" && i + 1 < args.Length)
                repoRoot = args[++i];
        }

        try
        {
            BenchmarkRunner.RunExperiment(profile, studentNumber, repoRoot);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Помилка виконання: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static string FindRepoRoot()
    {
        string cur = Directory.GetCurrentDirectory();
        while (!string.IsNullOrEmpty(cur))
        {
            if (File.Exists(Path.Combine(cur, "KursovaKP08.sln")) || Directory.Exists(Path.Combine(cur, "src")))
                return cur;
            DirectoryInfo? parent = Directory.GetParent(cur);
            if (parent == null) break;
            cur = parent.FullName;
        }
        return Directory.GetCurrentDirectory();
    }
}
