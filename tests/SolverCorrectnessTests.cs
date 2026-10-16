using System;
using System.Collections.Generic;
using KursovaKP08.Analysis;
using KursovaKP08.Generators;
using KursovaKP08.MethodA;
using KursovaKP08.MethodB;
using KursovaKP08.Reference;
using Xunit;

namespace KursovaKP08.Tests;

/// <summary>
/// Модульні тести коректності алгоритмів та статистичних функцій (розділ 6.3 методичних вказівок).
/// </summary>
public class SolverCorrectnessTests
{
    [Fact]
    public void ManualMinimalInstance_AllSolversFindValidCover_AndMilpIsOptimal()
    {
        // Ручний екземпляр з n = 4 точками {0,1,2,3} та m = 4 датчиками:
        // S0 = {0,1} (cost 2.0), S1 = {2,3} (cost 2.0) -> оптимум S0 + S1 = 4.0
        // S2 = {0,1,2} (cost 2.7), S3 = {3} (cost 2.0) -> жадібний бере S2 (2.7/3=0.9 < 2.0/2=1.0), потім S3 -> 4.7
        var s2p = new List<List<int>>
        {
            new() { 0, 1 },
            new() { 2, 3 },
            new() { 0, 1, 2 },
            new() { 3 }
        };
        double[] costs = { 2.0, 2.0, 2.7, 2.0 };
        var instance = InstanceGenerators.BuildInstance("Manual_4pts", "Manual", 4, 1, 1008, costs, s2p);

        var greedyRes = new GreedySolver().Solve(instance);
        var lpThreshRes = new LpThresholdRoundingSolver().Solve(instance);
        var lpRandRes = new LpRandomizedRoundingSolver().Solve(instance, 1008);
        var milpRes = new ExactMilpSolver().Solve(instance);

        Assert.True(greedyRes.IsFeasible);
        Assert.True(lpThreshRes.IsFeasible);
        Assert.True(lpRandRes.IsFeasible);
        Assert.True(milpRes.IsFeasible);
        Assert.True(milpRes.IsProvenOptimal);

        Assert.Equal(4.0, milpRes.TotalCost, 5);
        Assert.Equal(4.7, greedyRes.TotalCost, 5);
        Assert.Equal(4.0, lpThreshRes.TotalCost, 5);
    }

    [Fact]
    public void AdversarialInstance_GreedyApproximationExceedsLpRounding_AndRespectsHarmonicBound()
    {
        var instance = InstanceGenerators.GenerateAdversarialGreedy(levelP: 5, numBlocks: 2, seed: 1008, coverageK: 1);

        var greedyRes = new GreedySolver().Solve(instance);
        var lpThreshRes = new LpThresholdRoundingSolver().Solve(instance);
        var milpRes = new ExactMilpSolver().Solve(instance);

        Assert.True(greedyRes.IsFeasible);
        Assert.True(lpThreshRes.IsFeasible);
        Assert.True(milpRes.IsFeasible);

        // На адверсарному екземплярі жадібний алгоритм суттєво програє LP-округленню
        Assert.True(greedyRes.TotalCost > lpThreshRes.TotalCost * 1.45);
        // Але не перевищує теоретичну верхню межу H(d) * OPT
        Assert.True(greedyRes.TotalCost <= instance.HarmonicBoundHd * milpRes.TotalCost + 1e-6);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void GeometricObstacles_KCoverage_AllMethodsProduceValidKCover(int k)
    {
        var instance = InstanceGenerators.GenerateGeometric(
            numPoints: 75,
            numSensors: 45,
            radius: 26.0,
            withObstacles: true,
            coverageK: k,
            seed: 1008 + k);

        var greedyRes = new GreedySolver().Solve(instance);
        var lpThreshRes = new LpThresholdRoundingSolver().Solve(instance);
        var lpRandRes = new LpRandomizedRoundingSolver().Solve(instance, 2026);
        var milpRes = new ExactMilpSolver(5.0).Solve(instance);

        Assert.True(greedyRes.IsFeasible);
        Assert.True(lpThreshRes.IsFeasible);
        Assert.True(lpRandRes.IsFeasible);
        Assert.True(milpRes.IsFeasible);

        // Вартість кожного евристичного/апроксимаційного методу не менша за нижню межу LP*
        double lpBound = lpThreshRes.LpLowerBound ?? 0.0;
        Assert.True(greedyRes.TotalCost + 1e-6 >= lpBound);
        Assert.True(lpThreshRes.TotalCost + 1e-6 >= lpBound);
        Assert.True(lpRandRes.TotalCost + 1e-6 >= lpBound);
    }

    [Fact]
    public void StatisticalFunctions_MatchAppendixDReferenceValues()
    {
        // Перевірка еталонних значень із Додатка Д методичних вказівок:
        // A12 = 1.0 для повністю розділених вибірок і 0.5 для однакових;
        // поправка Холма для p = [0.01, 0.04, 0.03, 0.20] дає [0.04, 0.09, 0.09, 0.20].
        double a12Separated = StatsAndPlots.VarghaDelaneyA12(new[] { 10.0, 11.0, 12.0 }, new[] { 1.0, 2.0, 3.0 });
        double a12Identical = StatsAndPlots.VarghaDelaneyA12(new[] { 1.0, 2.0, 3.0 }, new[] { 1.0, 2.0, 3.0 });

        Assert.Equal(1.0, a12Separated, 6);
        Assert.Equal(0.5, a12Identical, 6);

        double[] adj = StatsAndPlots.HolmAdjust(new[] { 0.01, 0.04, 0.03, 0.20 });
        Assert.Equal(0.04, adj[0], 6);
        Assert.Equal(0.09, adj[1], 6);
        Assert.Equal(0.09, adj[2], 6);
        Assert.Equal(0.20, adj[3], 6);
    }
}
