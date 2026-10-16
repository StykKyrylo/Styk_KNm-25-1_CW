using System;
using System.Collections.Generic;
using System.Diagnostics;
using KursovaKP08.Reference;

namespace KursovaKP08.MethodA;

/// <summary>
/// Підхід A: Жадібний алгоритм покриття множини (Chvátal, 1979; узагальнення на k-покриття за Dobson, 1982).
/// Використовує чергу з пріоритетами та ліниве оновлення (lazy evaluation) питомих вартостей c_j / |S~_j|,
/// де |S~_j| — кількість точок у S_j, чиє поточне покриття менше за k.
/// Теоретична гарантія якості: H(d) = sum_{r=1}^d 1/r <= ln(d) + 1.
/// </summary>
public sealed class GreedySolver : ISensorPlacementSolver
{
    public string Name => "Greedy";
    public bool IsStochastic => false;

    public SolverResult Solve(SetCoverInstance instance, int runSeed = 0)
    {
        var swSetup = Stopwatch.StartNew();

        int n = instance.NumPoints;
        int m = instance.NumSensors;
        int k = instance.CoverageMultiplicityK;

        int[] coverage = new int[n];
        int[] effectiveGain = new int[m];
        bool[] selectedMask = new bool[m];

        var pq = new PriorityQueue<int, double>(m);
        for (int j = 0; j < m; j++)
        {
            int gain = instance.SensorToPoints[j].Length;
            effectiveGain[j] = gain;
            if (gain > 0)
            {
                // Невеликий детермінований tie-breaker за індексом для стабільності порядку
                double priority = (instance.Costs[j] / gain) + (j * 1e-13);
                pq.Enqueue(j, priority);
            }
        }

        int totalDeficit = n * k;
        var selectedList = new List<int>();
        double totalCost = 0.0;
        int iterations = 0;

        swSetup.Stop();
        var swSolve = Stopwatch.StartNew();

        while (totalDeficit > 0 && pq.Count > 0)
        {
            int j = pq.Dequeue();
            if (selectedMask[j]) continue;

            // Лінива перевірка: перераховуємо актуальний приріст для датчика j
            int currentGain = 0;
            int[] pts = instance.SensorToPoints[j];
            for (int idx = 0; idx < pts.Length; idx++)
            {
                if (coverage[pts[idx]] < k)
                    currentGain++;
            }

            if (currentGain == 0)
            {
                effectiveGain[j] = 0;
                continue;
            }

            // Якщо приріст зменшився порівняно з тим, що був під час вставки в купу,
            // оновлюємо пріоритет і повертаємо в чергу
            if (currentGain < effectiveGain[j])
            {
                effectiveGain[j] = currentGain;
                double newPriority = (instance.Costs[j] / currentGain) + (j * 1e-13);
                pq.Enqueue(j, newPriority);
                continue;
            }

            // Вибираємо датчик j
            selectedMask[j] = true;
            selectedList.Add(j);
            totalCost += instance.Costs[j];
            iterations++;

            for (int idx = 0; idx < pts.Length; idx++)
            {
                int pt = pts[idx];
                if (coverage[pt] < k)
                {
                    coverage[pt]++;
                    totalDeficit--;
                }
            }
        }

        swSolve.Stop();

        var (isValid, _, _, _) = SolutionVerifier.Verify(instance, selectedList, totalCost);

        return new SolverResult
        {
            MethodName = Name,
            SelectedSensors = selectedList.ToArray(),
            TotalCost = totalCost,
            SetupTimeMs = swSetup.Elapsed.TotalMilliseconds,
            SolveTimeMs = swSolve.Elapsed.TotalMilliseconds,
            PeakMemoryBytes = GC.GetTotalMemory(false),
            IsFeasible = isValid,
            IsProvenOptimal = false,
            LpLowerBound = null,
            IterationsOrNodes = iterations,
            Notes = $"H(d)={instance.HarmonicBoundHd:F3}"
        };
    }

    /// <summary>
    /// Допоміжна процедура жадібної добудови часткового розв'язку (використовується після рандомізованого округлення).
    /// </summary>
    public static void CompleteGreedily(
        SetCoverInstance instance,
        bool[] selectedMask,
        int[] coverage,
        List<int> selectedList,
        ref double totalCost)
    {
        int n = instance.NumPoints;
        int m = instance.NumSensors;
        int k = instance.CoverageMultiplicityK;

        int totalDeficit = 0;
        for (int i = 0; i < n; i++)
        {
            if (coverage[i] < k)
                totalDeficit += (k - coverage[i]);
        }

        if (totalDeficit == 0) return;

        int[] effectiveGain = new int[m];
        var pq = new PriorityQueue<int, double>();

        for (int j = 0; j < m; j++)
        {
            if (selectedMask[j]) continue;
            int gain = 0;
            foreach (int pt in instance.SensorToPoints[j])
            {
                if (coverage[pt] < k) gain++;
            }
            effectiveGain[j] = gain;
            if (gain > 0)
            {
                pq.Enqueue(j, (instance.Costs[j] / gain) + (j * 1e-13));
            }
        }

        while (totalDeficit > 0 && pq.Count > 0)
        {
            int j = pq.Dequeue();
            if (selectedMask[j]) continue;

            int currentGain = 0;
            foreach (int pt in instance.SensorToPoints[j])
            {
                if (coverage[pt] < k) currentGain++;
            }

            if (currentGain == 0) continue;
            if (currentGain < effectiveGain[j])
            {
                effectiveGain[j] = currentGain;
                pq.Enqueue(j, (instance.Costs[j] / currentGain) + (j * 1e-13));
                continue;
            }

            selectedMask[j] = true;
            selectedList.Add(j);
            totalCost += instance.Costs[j];

            foreach (int pt in instance.SensorToPoints[j])
            {
                if (coverage[pt] < k)
                {
                    coverage[pt]++;
                    totalDeficit--;
                }
            }
        }
    }
}
