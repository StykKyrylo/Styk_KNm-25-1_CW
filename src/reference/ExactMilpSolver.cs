using System;
using System.Collections.Generic;
using System.Diagnostics;
using Google.OrTools.LinearSolver;

namespace KursovaKP08.Reference;

/// <summary>
/// Еталонний точний MILP-розв'язувач та обчислювач нижньої межі LP-релаксації
/// на базі Google OR-Tools (SCIP / CBC для MILP та GLOP для неперервної LP-релаксації).
/// </summary>
public sealed class ExactMilpSolver : ISensorPlacementSolver
{
    private readonly double _timeLimitSeconds;

    public ExactMilpSolver(double timeLimitSeconds = 30.0)
    {
        _timeLimitSeconds = timeLimitSeconds;
    }

    public string Name => "Exact-MILP";
    public bool IsStochastic => false;

    public SolverResult Solve(SetCoverInstance instance, int runSeed = 0)
    {
        var swSetup = Stopwatch.StartNew();

        // Використовуємо CBC_MIXED_INTEGER_PROGRAMMING (або SCIP), вбудований у Google.OrTools
        Solver solver = Solver.CreateSolver("CBC_MIXED_INTEGER_PROGRAMMING")
                        ?? Solver.CreateSolver("SCIP")
                        ?? throw new InvalidOperationException("OR-Tools MILP solver not available.");

        solver.SetTimeLimit((long)(_timeLimitSeconds * 1000));
        // Фіксуємо 1 потік для чесного порівняння з однопотоковими алгоритмами (розділ 5.3)
        solver.SetNumThreads(1);

        int m = instance.NumSensors;
        int n = instance.NumPoints;
        int k = instance.CoverageMultiplicityK;

        Variable[] x = new Variable[m];
        Objective objective = solver.Objective();
        for (int j = 0; j < m; j++)
        {
            x[j] = solver.MakeIntVar(0.0, 1.0, $"x_{j}");
            objective.SetCoefficient(x[j], instance.Costs[j]);
        }
        objective.SetMinimization();

        for (int i = 0; i < n; i++)
        {
            Constraint ct = solver.MakeConstraint(k, double.PositiveInfinity, $"cov_{i}");
            foreach (int j in instance.PointToSensors[i])
            {
                ct.SetCoefficient(x[j], 1.0);
            }
        }

        swSetup.Stop();

        var swSolve = Stopwatch.StartNew();
        Solver.ResultStatus status = solver.Solve();
        swSolve.Stop();

        bool hasSolution = status == Solver.ResultStatus.OPTIMAL || status == Solver.ResultStatus.FEASIBLE;
        bool isOptimal = status == Solver.ResultStatus.OPTIMAL;

        var selected = new List<int>();
        double totalCost = 0.0;

        if (hasSolution)
        {
            for (int j = 0; j < m; j++)
            {
                if (x[j].SolutionValue() > 0.5)
                {
                    selected.Add(j);
                    totalCost += instance.Costs[j];
                }
            }
        }

        double bestBound = hasSolution ? objective.BestBound() : 0.0;
        var (isValid, _, _, _) = hasSolution
            ? SolutionVerifier.Verify(instance, selected, totalCost)
            : (false, 0.0, n, "No solution");

        return new SolverResult
        {
            MethodName = Name,
            SelectedSensors = selected.ToArray(),
            TotalCost = totalCost,
            SetupTimeMs = swSetup.Elapsed.TotalMilliseconds,
            SolveTimeMs = swSolve.Elapsed.TotalMilliseconds,
            PeakMemoryBytes = GC.GetTotalMemory(false),
            IsFeasible = isValid,
            IsProvenOptimal = isOptimal && isValid,
            LpLowerBound = bestBound,
            IterationsOrNodes = (int)solver.Nodes(),
            Notes = $"Status={status}"
        };
    }

    /// <summary>
    /// Розв'язує неперервну LP-релаксацію (0 <= x_j <= 1) методом симплекса (GLOP).
    /// Повертає оптимальні дробові значення x*, значення LP* та час побудови й розв'язання.
    /// </summary>
    public static (double[] FractionalX, double LpObjective, double SetupMs, double SolveMs, bool Success) SolveLpRelaxation(
        SetCoverInstance instance,
        double timeLimitSeconds = 30.0)
    {
        var swSetup = Stopwatch.StartNew();
        Solver solver = Solver.CreateSolver("GLOP_LINEAR_PROGRAMMING")
                        ?? throw new InvalidOperationException("GLOP LP solver not available.");
        solver.SetTimeLimit((long)(_ClampTime(timeLimitSeconds) * 1000));
        solver.SetNumThreads(1);

        int m = instance.NumSensors;
        int n = instance.NumPoints;
        int k = instance.CoverageMultiplicityK;

        Variable[] x = new Variable[m];
        Objective objective = solver.Objective();
        for (int j = 0; j < m; j++)
        {
            x[j] = solver.MakeNumVar(0.0, 1.0, $"x_{j}");
            objective.SetCoefficient(x[j], instance.Costs[j]);
        }
        objective.SetMinimization();

        for (int i = 0; i < n; i++)
        {
            Constraint ct = solver.MakeConstraint(k, double.PositiveInfinity, $"cov_{i}");
            foreach (int j in instance.PointToSensors[i])
            {
                ct.SetCoefficient(x[j], 1.0);
            }
        }
        swSetup.Stop();

        var swSolve = Stopwatch.StartNew();
        Solver.ResultStatus status = solver.Solve();
        swSolve.Stop();

        bool ok = status == Solver.ResultStatus.OPTIMAL || status == Solver.ResultStatus.FEASIBLE;
        double[] sol = new double[m];
        double obj = 0.0;
        if (ok)
        {
            for (int j = 0; j < m; j++)
            {
                sol[j] = Math.Clamp(x[j].SolutionValue(), 0.0, 1.0);
                obj += sol[j] * instance.Costs[j];
            }
        }

        return (sol, obj, swSetup.Elapsed.TotalMilliseconds, swSolve.Elapsed.TotalMilliseconds, ok);
    }

    private static double _ClampTime(double sec) => Math.Max(1.0, sec);
}
