using System;
using System.Collections.Generic;
using System.Linq;

namespace KursovaKP08.Reference;

/// <summary>
/// Незалежний верифікатор допустимості та вартості розв'язку (розділ 6.3 методичних вказівок).
/// </summary>
public static class SolutionVerifier
{
    public static (bool IsValid, double RecomputedCost, int UncoveredCount, string ErrorMessage) Verify(
        SetCoverInstance instance,
        IReadOnlyCollection<int> selectedSensors,
        double reportedCost,
        double costTolerance = 1e-6)
    {
        if (selectedSensors == null)
            return (false, 0.0, instance.NumPoints, "SelectedSensors is null");

        var unique = new HashSet<int>();
        double sumCost = 0.0;
        foreach (int j in selectedSensors)
        {
            if (j < 0 || j >= instance.NumSensors)
                return (false, sumCost, instance.NumPoints, $"Sensor index {j} out of range [0, {instance.NumSensors})");
            if (!unique.Add(j))
                return (false, sumCost, instance.NumPoints, $"Duplicate sensor index {j} in solution");
            sumCost += instance.Costs[j];
        }

        int[] coverage = new int[instance.NumPoints];
        foreach (int j in unique)
        {
            foreach (int pt in instance.SensorToPoints[j])
            {
                coverage[pt]++;
            }
        }

        int uncovered = 0;
        for (int i = 0; i < instance.NumPoints; i++)
        {
            if (coverage[i] < instance.CoverageMultiplicityK)
                uncovered++;
        }

        if (uncovered > 0)
            return (false, sumCost, uncovered, $"{uncovered} points have coverage < k={instance.CoverageMultiplicityK}");

        if (Math.Abs(sumCost - reportedCost) > costTolerance * Math.Max(1.0, sumCost))
            return (false, sumCost, 0, $"Reported cost {reportedCost:F6} != recomputed {sumCost:F6}");

        return (true, sumCost, 0, string.Empty);
    }
}
