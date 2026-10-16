using System;
using System.Collections.Generic;
using System.Linq;

namespace KursovaKP08.Reference;

/// <summary>
/// Екземпляр задачі оптимального розміщення датчиків (зважене покриття множини з k-резервуванням).
/// Дано множину контрольних точок U = {0, ..., n-1} та m кандидатних позицій датчиків S_0, ..., S_{m-1}.
/// </summary>
public sealed class SetCoverInstance
{
    public required string InstanceId { get; init; }
    public required string Family { get; init; } // Plane, MineObstacles, Adversarial, RealMinePlan
    public required int NumPoints { get; init; } // n = |U|
    public required int NumSensors { get; init; } // m
    public required int CoverageMultiplicityK { get; init; } // k >= 1
    public required int Seed { get; init; }
    public required double[] Costs { get; init; } // c_j for j in 0..m-1
    public required int[][] SensorToPoints { get; init; } // S_j subset of U
    public required int[][] PointToSensors { get; init; } // { j : i in S_j }

    // Попередньо обчислені структурні характеристики екземпляра
    public int MaxSetSizeD => SensorToPoints.Length == 0 ? 0 : SensorToPoints.Max(s => s.Length);
    public int MaxElementFrequencyF => PointToSensors.Length == 0 ? 0 : PointToSensors.Max(p => p.Length);
    public int MinElementFrequency => PointToSensors.Length == 0 ? 0 : PointToSensors.Min(p => p.Length);

    /// <summary>
    /// Гармонічне число H(d) = sum_{r=1}^d (1/r) — теоретична гарантія жадібного алгоритму.
    /// </summary>
    public double HarmonicBoundHd
    {
        get
        {
            int d = MaxSetSizeD;
            double sum = 0.0;
            for (int r = 1; r <= d; r++) sum += 1.0 / r;
            return sum;
        }
    }

    /// <summary>
    /// Теоретична гарантія детермінованого порогового LP-округлення:
    /// f для k=1, або (f - k + 1) для k-покриття без повторів з порогом 1 / (f - k + 1).
    /// </summary>
    public double LpThresholdBound => Math.Max(1, MaxElementFrequencyF - CoverageMultiplicityK + 1);

    public bool IsSolvable() => MinElementFrequency >= CoverageMultiplicityK;
}

/// <summary>
/// Результат роботи алгоритму розміщення датчиків.
/// </summary>
public sealed class SolverResult
{
    public required string MethodName { get; init; }
    public required int[] SelectedSensors { get; init; }
    public required double TotalCost { get; init; }
    public required double SetupTimeMs { get; init; }
    public required double SolveTimeMs { get; init; }
    public double TotalTimeMs => SetupTimeMs + SolveTimeMs;
    public required long PeakMemoryBytes { get; init; }
    public required bool IsFeasible { get; init; }
    public required bool IsProvenOptimal { get; init; }
    public double? LpLowerBound { get; init; }
    public int IterationsOrNodes { get; init; }
    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// Спільний інтерфейс для всіх досліджуваних алгоритмів (запорука чесного порівняння, розділ 4.4).
/// </summary>
public interface ISensorPlacementSolver
{
    string Name { get; }
    bool IsStochastic { get; }
    SolverResult Solve(SetCoverInstance instance, int runSeed = 0);
}
