using System;
using System.Collections.Generic;
using System.Diagnostics;
using KursovaKP08.MethodA;
using KursovaKP08.Reference;

namespace KursovaKP08.MethodB;

/// <summary>
/// Підхід B2: LP-релаксація + рандомізоване округлення з перевіркою й жадібною добудовою
/// та усуненням надлишковості (Raghavan & Thompson, 1987; Williamson & Shmoys, 2011).
/// Кожен датчик j включається незалежно з імовірністю p_j = min(1, alpha * x*_j),
/// після чого точки з покриттям < k добудовуються жадібним алгоритмом, а зайві датчики
/// (чиє видалення не порушує k-покриття) відсікаються у порядку спадання вартості.
/// </summary>
public sealed class LpRandomizedRoundingSolver : ISensorPlacementSolver
{
    private readonly double _scaleFactorAlpha;

    public LpRandomizedRoundingSolver(double scaleFactorAlpha = 1.25)
    {
        _scaleFactorAlpha = scaleFactorAlpha;
    }

    public string Name => "LP-Randomized";
    public bool IsStochastic => true;

    public SolverResult Solve(SetCoverInstance instance, int runSeed = 0)
    {
        var (xStar, lpObj, setupMs, lpSolveMs, ok) = ExactMilpSolver.SolveLpRelaxation(instance);

        var swRound = Stopwatch.StartNew();
        var rng = new Random(runSeed);

        int n = instance.NumPoints;
        int m = instance.NumSensors;

        bool[] selectedMask = new bool[m];
        int[] coverage = new int[n];
        var selectedList = new List<int>();
        double totalCost = 0.0;

        if (ok)
        {
            for (int j = 0; j < m; j++)
            {
                if (xStar[j] <= 1e-9) continue;
                double prob = Math.Min(1.0, _scaleFactorAlpha * xStar[j]);
                if (rng.NextDouble() < prob)
                {
                    selectedMask[j] = true;
                    selectedList.Add(j);
                    totalCost += instance.Costs[j];
                    foreach (int pt in instance.SensorToPoints[j])
                    {
                        coverage[pt]++;
                    }
                }
            }
        }

        // Перевірка допустимості та жадібна добудова для точок з покриттям < k
        GreedySolver.CompleteGreedily(instance, selectedMask, coverage, selectedList, ref totalCost);

        // Очищення надлишково вибраних датчиків (reverse-delete pruning)
        LpThresholdRoundingSolver.PruneRedundantSensors(instance, selectedMask, coverage, selectedList, ref totalCost);

        swRound.Stop();

        var (isValid, _, _, _) = SolutionVerifier.Verify(instance, selectedList, totalCost);

        return new SolverResult
        {
            MethodName = Name,
            SelectedSensors = selectedList.ToArray(),
            TotalCost = totalCost,
            SetupTimeMs = setupMs,
            SolveTimeMs = lpSolveMs + swRound.Elapsed.TotalMilliseconds,
            PeakMemoryBytes = GC.GetTotalMemory(false),
            IsFeasible = isValid,
            IsProvenOptimal = false,
            LpLowerBound = lpObj,
            IterationsOrNodes = selectedList.Count,
            Notes = $"Seed={runSeed}, alpha={_scaleFactorAlpha:F2}, LP*={lpObj:F3}"
        };
    }

    /// <summary>
    /// Оптимізований пакетний запуск R незалежних спроб рандомізованого округлення
    /// на одному вже обчисленому розв'язку LP-релаксації (щоб не розв'язувати ту саму LP 30 разів підряд).
    /// Час розв'язання LP чесно додається до часу кожного запуску.
    /// </summary>
    public List<SolverResult> SolveMultipleRuns(SetCoverInstance instance, int numRuns, int baseSeed)
    {
        var (xStar, lpObj, setupMs, lpSolveMs, ok) = ExactMilpSolver.SolveLpRelaxation(instance);
        var results = new List<SolverResult>(numRuns);

        int n = instance.NumPoints;
        int m = instance.NumSensors;

        for (int r = 0; r < numRuns; r++)
        {
            int runSeed = baseSeed + r * 7919;
            var swRound = Stopwatch.StartNew();
            var rng = new Random(runSeed);

            bool[] selectedMask = new bool[m];
            int[] coverage = new int[n];
            var selectedList = new List<int>();
            double totalCost = 0.0;

            if (ok)
            {
                for (int j = 0; j < m; j++)
                {
                    if (xStar[j] <= 1e-9) continue;
                    double prob = Math.Min(1.0, _scaleFactorAlpha * xStar[j]);
                    if (rng.NextDouble() < prob)
                    {
                        selectedMask[j] = true;
                        selectedList.Add(j);
                        totalCost += instance.Costs[j];
                        foreach (int pt in instance.SensorToPoints[j])
                        {
                            coverage[pt]++;
                        }
                    }
                }
            }

            GreedySolver.CompleteGreedily(instance, selectedMask, coverage, selectedList, ref totalCost);
            LpThresholdRoundingSolver.PruneRedundantSensors(instance, selectedMask, coverage, selectedList, ref totalCost);
            swRound.Stop();

            var (isValid, _, _, _) = SolutionVerifier.Verify(instance, selectedList, totalCost);

            results.Add(new SolverResult
            {
                MethodName = Name,
                SelectedSensors = selectedList.ToArray(),
                TotalCost = totalCost,
                SetupTimeMs = setupMs,
                SolveTimeMs = lpSolveMs + swRound.Elapsed.TotalMilliseconds,
                PeakMemoryBytes = GC.GetTotalMemory(false),
                IsFeasible = isValid,
                IsProvenOptimal = false,
                LpLowerBound = lpObj,
                IterationsOrNodes = selectedList.Count,
                Notes = $"Run={r + 1}/{numRuns}, Seed={runSeed}"
            });
        }

        return results;
    }
}
