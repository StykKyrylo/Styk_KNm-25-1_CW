using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using KursovaKP08.Analysis;
using KursovaKP08.Generators;
using KursovaKP08.MethodA;
using KursovaKP08.MethodB;
using KursovaKP08.Reference;

namespace KursovaKP08.Bench;

/// <summary>
/// Вимірювальна обв'язка обчислювального експерименту (розділ 5.3–5.4 методичних вказівок):
/// - Перевірка коректності на еталонних входах перед стартом вимірювань;
/// - Прогрів JIT-компілятора та кешів (холості прогони відкидаються);
/// - Чергування методів у випадковому порядку на кожному екземплярі;
/// - Не менше 10 повторів для детермінованих методів і 30 запусків із різними зернами для стохастичних;
/// - Одиниця аналізу — екземпляр (медіана за повторами/запусками);
/// - Запис сирих вимірювань у CSV та протоколу апаратної конфігурації.
/// </summary>
public static class BenchmarkRunner
{
    public static void RunSelfCheckBeforeBenchmark(int baseSeed)
    {
        Console.WriteLine("[1/4] Перевірка коректності реалізацій на еталонних екземплярах...");
        var greedy = new GreedySolver();
        var lpThresh = new LpThresholdRoundingSolver();
        var lpRand = new LpRandomizedRoundingSolver();
        var milp = new ExactMilpSolver(10.0);

        // Перевірка 1: Адверсарний екземпляр Хватала
        var adv = InstanceGenerators.GenerateAdversarialGreedy(levelP: 5, numBlocks: 2, seed: baseSeed, coverageK: 1);
        var resG = greedy.Solve(adv);
        var resT = lpThresh.Solve(adv);
        var resM = milp.Solve(adv);

        if (!resG.IsFeasible || !resT.IsFeasible || !resM.IsFeasible)
            throw new InvalidOperationException("Помилка допустимості на адверсарному екземплярі!");

        if (resG.TotalCost <= resM.TotalCost * 1.3)
            throw new InvalidOperationException("Адверсарний екземпляр не демонструє перевагу оптимуму над жадібним методом!");

        // Перевірка 2: Геометричний екземпляр із перешкодами та k=2
        var geo = InstanceGenerators.GenerateGeometric(80, 50, 24.0, withObstacles: true, coverageK: 2, seed: baseSeed + 1);
        foreach (var s in new ISensorPlacementSolver[] { greedy, lpThresh, lpRand, milp })
        {
            var r = s.Solve(geo, baseSeed + 99);
            if (!r.IsFeasible)
                throw new InvalidOperationException($"Метод {s.Name} видав недопустимий розв'язок на k=2!");
        }

        Console.WriteLine("      Усі передстартові тести коректності пройдено успішно.");
    }

    public static void RunExperiment(string profile, int studentNumber, string repoRoot)
    {
        int topicNumber = 8;
        int baseSeed = 1000 * studentNumber + topicNumber; // Формула з п. 2.3: seed = 1000 * номер + 8
        bool isFull = string.Equals(profile, "full", StringComparison.OrdinalIgnoreCase);

        int instancesPerConfig = isFull ? 30 : 8;
        int detRepeats = isFull ? 10 : 5;
        int stochRuns = isFull ? 30 : 20;

        Console.WriteLine($"==================================================================");
        Console.WriteLine($"Курсова робота КР-08 | Профіль: {profile.ToUpperInvariant()} | Seed: {baseSeed} (студент #{studentNumber})");
        Console.WriteLine($"Екземплярів на конфігурацію: {instancesPerConfig} | Повторів дет.: {detRepeats} | Запусків стох.: {stochRuns}");
        Console.WriteLine($"==================================================================");

        RunSelfCheckBeforeBenchmark(baseSeed);

        string dataDir = Path.Combine(repoRoot, "data");
        string rawDir = Path.Combine(repoRoot, "results", "raw");
        string resultsDir = Path.Combine(repoRoot, "results");
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(rawDir);

        string realPlanJson = Path.Combine(dataDir, "mine_horizon_plan.json");
        InstanceGenerators.EnsureRealMinePlanJson(realPlanJson);

        // Прогрів JIT
        Console.WriteLine("[2/4] Прогрів JIT-компілятора та бібліотеки OR-Tools...");
        var warmupInst = InstanceGenerators.GenerateGeometric(60, 40, 22.0, true, 1, baseSeed);
        new GreedySolver().Solve(warmupInst);
        new LpThresholdRoundingSolver().Solve(warmupInst);
        new LpRandomizedRoundingSolver().Solve(warmupInst, baseSeed);
        new ExactMilpSolver(2.0).Solve(warmupInst);

        // Формування набору тестових екземплярів (фіксується до вимірювань, п. 5.2)
        Console.WriteLine("[3/4] Генерація фіксованого набору бенчмарків та виконання вимірювань...");
        var benchmarkInstances = new List<SetCoverInstance>();

        // А) Основна серія порівняння якості й часу (Plane та MineObstacles, k=1)
        int[] mainSizes = isFull ? new[] { 100, 250, 500, 1000 } : new[] { 100, 300, 800 };
        foreach (int n in mainSizes)
        {
            int m = Math.Max(60, (int)(n * 0.65));
            double radius = 100.0 * Math.Sqrt(4.2 / Math.Max(20.0, m * 0.35));
            for (int idx = 0; idx < instancesPerConfig; idx++)
            {
                int s = baseSeed + n * 100 + idx;
                benchmarkInstances.Add(InstanceGenerators.GenerateGeometric(n, m, radius, withObstacles: false, coverageK: 1, seed: s));
                benchmarkInstances.Add(InstanceGenerators.GenerateGeometric(n, m, radius, withObstacles: true, coverageK: 1, seed: s + 50_000));
            }
        }

        // Б) Серія адверсарних («поганих») екземплярів Хватала–Джонсона (рівні p = 4, 5, 6, 7)
        int[] pLevels = isFull ? new[] { 4, 5, 6, 7 } : new[] { 4, 5, 6 };
        foreach (int p in pLevels)
        {
            for (int idx = 0; idx < instancesPerConfig; idx++)
            {
                int s = baseSeed + p * 1000 + idx;
                int blocks = Math.Max(2, (idx % 4) + 2);
                benchmarkInstances.Add(InstanceGenerators.GenerateAdversarialGreedy(p, blocks, s, coverageK: 1));
            }
        }

        // В) Серія на реальному плані шахтного горизонту (різна щільність контрольних точок)
        int[] densities = isFull ? new[] { 1, 2, 4 } : new[] { 1, 2 };
        foreach (int dens in densities)
        {
            for (int idx = 0; idx < instancesPerConfig; idx++)
            {
                int s = baseSeed + dens * 2000 + idx;
                benchmarkInstances.Add(InstanceGenerators.LoadOrGenerateRealMinePlan(realPlanJson, dens, coverageK: 1, seed: s));
            }
        }

        // Г) Серія k-покриття з резервуванням (k = 2, 3) на MineObstacles
        foreach (int kVal in new[] { 2, 3 })
        {
            int countK = isFull ? 15 : 6;
            for (int idx = 0; idx < countK; idx++)
            {
                int s = baseSeed + kVal * 5000 + idx;
                benchmarkInstances.Add(InstanceGenerators.GenerateGeometric(200, 130, 26.0, withObstacles: true, coverageK: kVal, seed: s));
            }
        }

        // Д) Серія масштабування на великих розмірностях до n = 10^5 (Greedy vs LP на середніх і великих n)
        int[] largeSizes = isFull ? new[] { 2500, 10000, 50000, 100000 } : new[] { 2500, 10000, 50000 };
        foreach (int nLarge in largeSizes)
        {
            int countLarge = isFull ? 5 : 3;
            int mLarge = Math.Min(4000, Math.Max(300, nLarge / 15));
            double rLarge = 22.0;
            for (int idx = 0; idx < countLarge; idx++)
            {
                int s = baseSeed + nLarge + idx;
                benchmarkInstances.Add(InstanceGenerators.GenerateGeometric(nLarge, mLarge, rLarge, withObstacles: false, coverageK: 1, seed: s));
            }
        }

        var greedySolver = new GreedySolver();
        var lpThreshSolver = new LpThresholdRoundingSolver(enableReverseDeletePruning: false);
        var lpRandSolver = new LpRandomizedRoundingSolver(scaleFactorAlpha: 1.25);
        var milpSolver = new ExactMilpSolver(timeLimitSeconds: isFull ? 10.0 : 4.0);

        string rawRunsCsv = Path.Combine(rawDir, "raw_runs.csv");
        string summaryCsv = Path.Combine(rawDir, "instance_summary.csv");

        using var rawWriter = new StreamWriter(rawRunsCsv, false, Encoding.UTF8);
        rawWriter.WriteLine("InstanceId,Family,NumPoints,NumSensors,CoverageK,Seed,Method,RunIndex,TotalCost,SetupMs,SolveMs,TotalMs,IsFeasible,IsOptimal,LpLowerBound");

        var summaryRows = new List<InstanceSummaryRow>();
        var orderRng = new Random(baseSeed + 777);

        int completed = 0;
        foreach (var inst in benchmarkInstances)
        {
            completed++;
            if (completed % 10 == 0 || completed == benchmarkInstances.Count)
            {
                Console.WriteLine($"      Опрацьовано екземплярів: {completed} / {benchmarkInstances.Count} (поточний: {inst.InstanceId})");
            }

            bool runLpAndMilp = inst.NumPoints <= 2500;
            bool runMilp = inst.NumPoints <= 1000;

            double lpBound = 0.0;
            double? exactOpt = null;

            if (runLpAndMilp)
            {
                var (_, lpObj, _, _, ok) = ExactMilpSolver.SolveLpRelaxation(inst, 15.0);
                if (ok) lpBound = lpObj;
            }

            if (runMilp)
            {
                var milpRes = milpSolver.Solve(inst);
                if (milpRes.IsFeasible && milpRes.IsProvenOptimal)
                    exactOpt = milpRes.TotalCost;
                if (lpBound <= 0 && milpRes.LpLowerBound.HasValue)
                    lpBound = milpRes.LpLowerBound.Value;

                rawWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},1,{7:F6},{8:F4},{9:F4},{10:F4},{11},{12},{13:F6}",
                    inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                    milpRes.MethodName, milpRes.TotalCost, milpRes.SetupTimeMs, milpRes.SolveTimeMs, milpRes.TotalTimeMs,
                    milpRes.IsFeasible, milpRes.IsProvenOptimal, lpBound));

                summaryRows.Add(new InstanceSummaryRow(
                    inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                    inst.MaxSetSizeD, inst.MaxElementFrequencyF, inst.HarmonicBoundHd, lpBound, exactOpt,
                    milpRes.MethodName, milpRes.TotalCost, 0.0,
                    lpBound > 1e-9 ? milpRes.TotalCost / lpBound : 1.0,
                    milpRes.SetupTimeMs, milpRes.SolveTimeMs, milpRes.TotalTimeMs, 0.0,
                    milpRes.PeakMemoryBytes, milpRes.IsFeasible ? 1.0 : 0.0));
            }

            // Випадковий порядок чергування детермінованих методів для усунення системного дрейфу
            var detMethods = new List<ISensorPlacementSolver> { greedySolver };
            if (runLpAndMilp) detMethods.Add(lpThreshSolver);

            var detRuns = detMethods.ToDictionary(m => m.Name, _ => new List<SolverResult>());
            int repCount = inst.NumPoints >= 20000 ? 5 : detRepeats;

            for (int rep = 1; rep <= repCount; rep++)
            {
                foreach (var solver in detMethods.OrderBy(_ => orderRng.Next()))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    var res = solver.Solve(inst, inst.Seed + rep);
                    detRuns[solver.Name].Add(res);

                    rawWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8:F6},{9:F4},{10:F4},{11:F4},{12},{13},{14:F6}",
                        inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                        res.MethodName, rep, res.TotalCost, res.SetupTimeMs, res.SolveTimeMs, res.TotalTimeMs,
                        res.IsFeasible, res.IsProvenOptimal, lpBound));
                }
            }

            foreach (var kv in detRuns)
            {
                var list = kv.Value;
                double medCost = StatsAndPlots.Median(list.Select(x => x.TotalCost).ToList());
                double iqrCost = StatsAndPlots.Iqr(list.Select(x => x.TotalCost).ToList());
                double medSetup = StatsAndPlots.Median(list.Select(x => x.SetupTimeMs).ToList());
                double medSolve = StatsAndPlots.Median(list.Select(x => x.SolveTimeMs).ToList());
                double medTotal = StatsAndPlots.Median(list.Select(x => x.TotalTimeMs).ToList());
                double iqrTotal = StatsAndPlots.Iqr(list.Select(x => x.TotalTimeMs).ToList());
                double denom = lpBound > 1e-9 ? lpBound : (exactOpt ?? medCost);

                summaryRows.Add(new InstanceSummaryRow(
                    inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                    inst.MaxSetSizeD, inst.MaxElementFrequencyF, inst.HarmonicBoundHd, lpBound, exactOpt,
                    kv.Key, medCost, iqrCost, medCost / Math.Max(1e-9, denom),
                    medSetup, medSolve, medTotal, iqrTotal,
                    list.Max(x => x.PeakMemoryBytes), list.Count(x => x.IsFeasible) / (double)list.Count));
            }

            // Стохастичний метод: LP-Randomized (30 незалежних запусків із різними зернами на екземпляр)
            if (runLpAndMilp)
            {
                var randList = lpRandSolver.SolveMultipleRuns(inst, stochRuns, inst.Seed * 31 + 17);
                for (int rIdx = 0; rIdx < randList.Count; rIdx++)
                {
                    var res = randList[rIdx];
                    rawWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0},{1},{2},{3},{4},{5},{6},{7},{8:F6},{9:F4},{10:F4},{11:F4},{12},{13},{14:F6}",
                        inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                        res.MethodName, rIdx + 1, res.TotalCost, res.SetupTimeMs, res.SolveTimeMs, res.TotalTimeMs,
                        res.IsFeasible, res.IsProvenOptimal, lpBound));
                }

                double medCost = StatsAndPlots.Median(randList.Select(x => x.TotalCost).ToList());
                double iqrCost = StatsAndPlots.Iqr(randList.Select(x => x.TotalCost).ToList());
                double medSetup = StatsAndPlots.Median(randList.Select(x => x.SetupTimeMs).ToList());
                double medSolve = StatsAndPlots.Median(randList.Select(x => x.SolveTimeMs).ToList());
                double medTotal = StatsAndPlots.Median(randList.Select(x => x.TotalTimeMs).ToList());
                double iqrTotal = StatsAndPlots.Iqr(randList.Select(x => x.TotalTimeMs).ToList());
                double denom = lpBound > 1e-9 ? lpBound : (exactOpt ?? medCost);

                summaryRows.Add(new InstanceSummaryRow(
                    inst.InstanceId, inst.Family, inst.NumPoints, inst.NumSensors, inst.CoverageMultiplicityK, inst.Seed,
                    inst.MaxSetSizeD, inst.MaxElementFrequencyF, inst.HarmonicBoundHd, lpBound, exactOpt,
                    lpRandSolver.Name, medCost, iqrCost, medCost / Math.Max(1e-9, denom),
                    medSetup, medSolve, medTotal, iqrTotal,
                    randList.Max(x => x.PeakMemoryBytes), randList.Count(x => x.IsFeasible) / (double)randList.Count));
            }
        }

        // Запис агрегованої таблиці по екземплярах (одиниця аналізу — екземпляр)
        using (var sumWriter = new StreamWriter(summaryCsv, false, Encoding.UTF8))
        {
            sumWriter.WriteLine("InstanceId,Family,NumPoints,NumSensors,CoverageK,Seed,MaxSetSizeD,MaxFreqF,HarmonicBoundHd,LpLowerBound,ExactOptCost,MethodName,MedianCost,IqrCost,MedianRatioToOptOrLp,MedianSetupMs,MedianSolveMs,MedianTotalMs,IqrTotalMs,PeakMemoryBytes,FeasibleRate");
            foreach (var r in summaryRows)
            {
                sumWriter.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7},{8:F4},{9:F6},{10},{11},{12:F6},{13:F6},{14:F6},{15:F4},{16:F4},{17:F4},{18:F4},{19},{20:F2}",
                    r.InstanceId, r.Family, r.NumPoints, r.NumSensors, r.CoverageK, r.Seed,
                    r.MaxSetSizeD, r.MaxFreqF, r.HarmonicBoundHd, r.LpLowerBound,
                    r.ExactOptCost.HasValue ? r.ExactOptCost.Value.ToString("F6", CultureInfo.InvariantCulture) : "",
                    r.MethodName, r.MedianCost, r.IqrCost, r.MedianRatioToOptOrLp,
                    r.MedianSetupMs, r.MedianSolveMs, r.MedianTotalMs, r.IqrTotalMs,
                    r.PeakMemoryBytes, r.FeasibleRate));
            }
        }

        WriteHardwareProtocol(Path.Combine(rawDir, "hardware_config.md"), profile, baseSeed);

        Console.WriteLine("[4/4] Статистичний аналіз (Вілкоксон, A12, Холм, бутстреп) і побудова SVG-графіків...");
        StatsAndPlots.GenerateFullReportAndFigures(summaryRows, resultsDir, baseSeed);
        Console.WriteLine($"Готово! Сирі дані: {rawDir}");
        Console.WriteLine($"Графіки: {Path.Combine(resultsDir, "figures")}");
        Console.WriteLine($"Зведені таблиці: {Path.Combine(resultsDir, "summary_tables.md")}");
    }

    private static void WriteHardwareProtocol(string path, string profile, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Протокол апаратної й програмної конфігурації (Додаток Г.2)");
        sb.AppendLine();
        sb.AppendLine("| Параметр | Значення |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Операційна система | {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}) |");
        sb.AppendLine($"| Логічні ядра процесора | {Environment.ProcessorCount} |");
        sb.AppendLine($"| Мова / рантайм і версія | C# 12 / {RuntimeInformation.FrameworkDescription} |");
        sb.AppendLine($"| Ключові бібліотеки й версії | Google.OrTools 9.11.4210 (GLOP LP, CBC/SCIP MILP) |");
        sb.AppendLine($"| Параметри збірки | Release (`Optimize=true`, ServerGC=false) |");
        sb.AppendLine($"| Кількість потоків розв'язувача | 1 потік (`SetNumThreads(1)`) |");
        sb.AppendLine($"| Профіль та базове зерно (seed) | `{profile}`, `seed = {seed}` |");
        sb.AppendLine($"| Дата та час вимірювання (UTC) | {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC |");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }
}
