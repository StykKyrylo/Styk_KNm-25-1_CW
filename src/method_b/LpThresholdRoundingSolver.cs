using System;
using System.Collections.Generic;
using System.Diagnostics;
using KursovaKP08.MethodA;
using KursovaKP08.Reference;

namespace KursovaKP08.MethodB;

/// <summary>
/// Підхід B1: LP-релаксація + детерміноване порогове округлення (Hochbaum, 1982; Vazirani, 2001).
/// Вибираються всі позиції датчиків з x*_j >= 1 / (f - k + 1) (при k=1 поріг становить 1/f),
/// де f = max_{i in U} |{j : i in S_j}| — максимальна кратність покриття точки.
/// Додатково має опційний етап видалення надлишкових датчиків у порядку спадання вартості (reverse-delete pruning).
/// </summary>
public sealed class LpThresholdRoundingSolver : ISensorPlacementSolver
{
    private readonly bool _enableReverseDeletePruning;

    public LpThresholdRoundingSolver(bool enableReverseDeletePruning = false)
    {
        _enableReverseDeletePruning = enableReverseDeletePruning;
    }

    public string Name => _enableReverseDeletePruning ? "LP-Threshold-Pruned" : "LP-Threshold";
    public bool IsStochastic => false;

    public SolverResult Solve(SetCoverInstance instance, int runSeed = 0)
    {
        var (xStar, lpObj, setupMs, lpSolveMs, ok) = ExactMilpSolver.SolveLpRelaxation(instance);

        var swRound = Stopwatch.StartNew();
        int n = instance.NumPoints;
        int m = instance.NumSensors;
        int k = instance.CoverageMultiplicityK;
        int f = Math.Max(1, instance.MaxElementFrequencyF);

        // Теоретичний поріг: для k-покриття без повторів поріг 1 / (f - k + 1) гарантує покриття.
        // Враховуємо 1e-9 для захисту від похибки округлення IEEE 754 у LP-розв'язувачі.
        double denom = Math.Max(1, f - k + 1);
        double threshold = (1.0 / denom) - 1e-9;

        bool[] selectedMask = new bool[m];
        int[] coverage = new int[n];
        var selectedList = new List<int>();
        double totalCost = 0.0;

        if (ok)
        {
            for (int j = 0; j < m; j++)
            {
                if (xStar[j] >= threshold)
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

        // Якщо через виродженість чи чисельний допуск якась точка лишилася непокритою — жадібна добудова
        GreedySolver.CompleteGreedily(instance, selectedMask, coverage, selectedList, ref totalCost);

        if (_enableReverseDeletePruning)
        {
            PruneRedundantSensors(instance, selectedMask, coverage, selectedList, ref totalCost);
        }

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
            Notes = $"Threshold=1/{denom:F0}, LP*={lpObj:F3}"
        };
    }

    public static void PruneRedundantSensors(
        SetCoverInstance instance,
        bool[] selectedMask,
        int[] coverage,
        List<int> selectedList,
        ref double totalCost)
    {
        int k = instance.CoverageMultiplicityK;
        // Сортуємо вибрані датчики за спаданням вартості, щоб першими прибирати найдорожчі зайві датчики
        selectedList.Sort((a, b) => instance.Costs[b].CompareTo(instance.Costs[a]));

        var kept = new List<int>(selectedList.Count);
        foreach (int j in selectedList)
        {
            bool canRemove = true;
            foreach (int pt in instance.SensorToPoints[j])
            {
                if (coverage[pt] <= k)
                {
                    canRemove = false;
                    break;
                }
            }

            if (canRemove)
            {
                selectedMask[j] = false;
                totalCost -= instance.Costs[j];
                foreach (int pt in instance.SensorToPoints[j])
                {
                    coverage[pt]--;
                }
            }
            else
            {
                kept.Add(j);
            }
        }

        kept.Sort();
        selectedList.Clear();
        selectedList.AddRange(kept);
    }
}
