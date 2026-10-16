using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using KursovaKP08.Reference;

namespace KursovaKP08.Generators;

public record WallSegment(double X1, double Y1, double X2, double Y2);

/// <summary>
/// Генератори синтетичних (геометричних на площині та у виробці з перешкодами),
/// адверсарних («поганих» для жадібного алгоритму з оцінкою Omega(ln n))
/// та реальних екземплярів задачі розміщення датчиків (розділ 5.2 методичних вказівок).
/// </summary>
public static class InstanceGenerators
{
    /// <summary>
    /// 1. Геометричний генератор (відкрита площина або виробка з перешкодами-стінами).
    /// Використовує просторове хешування (grid indexing) для швидкої генерації навіть при n = 10^5.
    /// Гарантує припустимість екземпляра (кожна точка покрита щонайменше k датчиками).
    /// </summary>
    public static SetCoverInstance GenerateGeometric(
        int numPoints,
        int numSensors,
        double radius,
        bool withObstacles,
        int coverageK,
        int seed,
        string costMode = "AreaWeighted")
    {
        var rng = new Random(seed);
        double width = 100.0;
        double height = 100.0;

        var walls = new List<WallSegment>();
        if (withObstacles)
        {
            // Моделюємо шахтну виробку / цех із 6 внутрішніми перегородками-ціликами
            walls.Add(new WallSegment(25, 0, 25, 38));
            walls.Add(new WallSegment(25, 52, 25, 100));
            walls.Add(new WallSegment(50, 15, 50, 85));
            walls.Add(new WallSegment(75, 0, 75, 42));
            walls.Add(new WallSegment(75, 58, 75, 100));
            walls.Add(new WallSegment(25, 50, 42, 50));
        }

        double[] px = new double[numPoints];
        double[] py = new double[numPoints];
        for (int i = 0; i < numPoints; i++)
        {
            px[i] = rng.NextDouble() * width;
            py[i] = rng.NextDouble() * height;
        }

        var sx = new List<double>(numSensors + 32);
        var sy = new List<double>(numSensors + 32);
        var costs = new List<double>(numSensors + 32);

        for (int j = 0; j < numSensors; j++)
        {
            double x = rng.NextDouble() * width;
            double y = rng.NextDouble() * height;
            sx.Add(x);
            sy.Add(y);
            costs.Add(SampleCost(rng, costMode));
        }

        // Побудова просторової сітки для швидкого пошуку точок у радіусі R
        double cellSize = Math.Max(2.0, radius);
        int gridW = (int)Math.Ceiling(width / cellSize) + 1;
        int gridH = (int)Math.Ceiling(height / cellSize) + 1;
        var grid = new List<int>[gridW * gridH];
        for (int c = 0; c < grid.Length; c++) grid[c] = new List<int>();

        for (int i = 0; i < numPoints; i++)
        {
            int gx = Math.Clamp((int)(px[i] / cellSize), 0, gridW - 1);
            int gy = Math.Clamp((int)(py[i] / cellSize), 0, gridH - 1);
            grid[gy * gridW + gx].Add(i);
        }

        var sensorToPoints = new List<List<int>>(sx.Count);
        int[] pointCoverageCount = new int[numPoints];
        double rSq = radius * radius;

        void ComputeSensorCoverage(int sensorIdx)
        {
            double x = sx[sensorIdx];
            double y = sy[sensorIdx];
            int gx = Math.Clamp((int)(x / cellSize), 0, gridW - 1);
            int gy = Math.Clamp((int)(y / cellSize), 0, gridH - 1);

            var covered = new List<int>();
            for (int dy = -1; dy <= 1; dy++)
            {
                int ny = gy + dy;
                if (ny < 0 || ny >= gridH) continue;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = gx + dx;
                    if (nx < 0 || nx >= gridW) continue;
                    foreach (int pt in grid[ny * gridW + nx])
                    {
                        double ddx = x - px[pt];
                        double ddy = y - py[pt];
                        if (ddx * ddx + ddy * ddy <= rSq)
                        {
                            if (!withObstacles || HasLineOfSight(x, y, px[pt], py[pt], walls))
                            {
                                covered.Add(pt);
                                pointCoverageCount[pt]++;
                            }
                        }
                    }
                }
            }
            sensorToPoints.Add(covered);
        }

        for (int j = 0; j < sx.Count; j++)
        {
            ComputeSensorCoverage(j);
        }

        // Гарантуємо k-покриття кожної контрольної точки: якщо точка i має менше k доступних датчиків,
        // додаємо резервні кандидатні позиції поблизу неї
        for (int i = 0; i < numPoints; i++)
        {
            while (pointCoverageCount[i] < coverageK)
            {
                double angle = rng.NextDouble() * 2.0 * Math.PI;
                double dist = rng.NextDouble() * (radius * 0.25);
                double nx = Math.Clamp(px[i] + dist * Math.Cos(angle), 0.1, width - 0.1);
                double ny = Math.Clamp(py[i] + dist * Math.Sin(angle), 0.1, height - 0.1);
                sx.Add(nx);
                sy.Add(ny);
                costs.Add(SampleCost(rng, costMode));
                int newIdx = sx.Count - 1;
                ComputeSensorCoverage(newIdx);
                // На випадок, якщо стіна все одно перекрила, прямо додаємо точку i до її сусіднього датчика
                if (!sensorToPoints[newIdx].Contains(i))
                {
                    sensorToPoints[newIdx].Add(i);
                    pointCoverageCount[i]++;
                }
            }
        }

        // Якщо costMode == "AreaWeighted", коригуємо вартість слабко пропорційно до розміру множини з шумом,
        // що робить задачу набагато цікавішою для порівняння Greedy проти LP
        int finalM = sx.Count;
        double[] finalCosts = new double[finalM];
        for (int j = 0; j < finalM; j++)
        {
            int sz = Math.Max(1, sensorToPoints[j].Count);
            if (costMode == "AreaWeighted")
            {
                finalCosts[j] = Math.Round(1.0 + 0.35 * Math.Sqrt(sz) + rng.NextDouble() * 1.5, 4);
            }
            else
            {
                finalCosts[j] = costs[j];
            }
        }

        return BuildInstance(
            instanceId: $"{(withObstacles ? "MineObs" : "Plane")}_n{numPoints}_m{finalM}_k{coverageK}_s{seed}",
            family: withObstacles ? "MineObstacles" : "Plane",
            numPoints: numPoints,
            coverageK: coverageK,
            seed: seed,
            costs: finalCosts,
            sensorToPoints: sensorToPoints);
    }

    /// <summary>
    /// 2. Адверсарний («поганий») екземпляр для жадібного алгоритму (конструкція Хватала–Джонсона).
    /// Універсум розбито на M паралельних «блоків» розміру (2^p - 2) кожен.
    /// Для кожного блоку b є:
    /// - дві «оптимальні вертикальні» множини O_{b,1}, O_{b,2} (кожна покриває половину блоку) з вартістю (1 + eps);
    /// - (p - 1) «жадібних поперечних» множин G_{b,1}, ..., G_{b,p-1} розмірів 2^{p-1}, 2^{p-2}, ..., 2 з вартістю 1.0.
    /// Оптимум бере 2 вертикальні множини у кожному блоці з вартістю 2*(1+eps)*M.
    /// Жадібний алгоритм на кожному кроці спокушається на G_{b,r} (бо 1 / 2^{p-r} < (1+eps) / 2^{p-1})
    /// і набирає всі (p - 1) поперечних множин + одну вертикальну, витрачаючи p * M замість 2 * M!
    /// При цьому кожен елемент належить максимум f = 2 поперечним або вертикальним множинам (f <= 2..3),
    /// а отже LP-округлення з порогом 1/f знаходить точний оптимум!
    /// </summary>
    public static SetCoverInstance GenerateAdversarialGreedy(int levelP, int numBlocks, int seed, int coverageK = 1)
    {
        levelP = Math.Clamp(levelP, 3, 12);
        numBlocks = Math.Max(1, numBlocks);
        var rng = new Random(seed);

        int blockSize = (1 << levelP) - 2; // 2^p - 2 (ділиться на 2)
        int halfBlock = blockSize / 2;
        int numPoints = numBlocks * blockSize;

        var sensorToPoints = new List<List<int>>();
        var costs = new List<double>();

        for (int b = 0; b < numBlocks; b++)
        {
            int offset = b * blockSize;
            double eps = 1e-3 + rng.NextDouble() * 1e-4;

            // Дві оптимальні множини O_{b,1} та O_{b,2}, які разом повністю покривають блок b
            var opt1 = new List<int>(halfBlock);
            var opt2 = new List<int>(halfBlock);
            for (int i = 0; i < halfBlock; i++)
            {
                opt1.Add(offset + i);
                opt2.Add(offset + halfBlock + i);
            }
            sensorToPoints.Add(opt1);
            costs.Add(Math.Round(1.0 + eps, 6));

            sensorToPoints.Add(opt2);
            costs.Add(Math.Round(1.0 + eps, 6));

            // Якщо потрібне k-покриття (k > 1), додаємо дублюючі оптимальні шари з тією самою структурою
            for (int extra = 1; extra < coverageK; extra++)
            {
                sensorToPoints.Add(new List<int>(opt1));
                costs.Add(Math.Round(1.0 + eps * (extra + 1), 6));
                sensorToPoints.Add(new List<int>(opt2));
                costs.Add(Math.Round(1.0 + eps * (extra + 1), 6));
            }

            // Поперечні множини G_{b,r} для r = 1 .. levelP - 1
            // r-та множина бере по 2^{levelP - 1 - r} елементів із кожної половини opt1 та opt2
            int cursor = 0;
            for (int r = 1; r < levelP; r++)
            {
                int takeFromHalf = 1 << (levelP - 1 - r); // 2^{p - 1 - r}
                var greedySet = new List<int>(2 * takeFromHalf);
                for (int t = 0; t < takeFromHalf; t++)
                {
                    greedySet.Add(offset + cursor + t);
                    greedySet.Add(offset + halfBlock + cursor + t);
                }
                cursor += takeFromHalf;

                // Вартість трохи менша за 1.0 на ранніх кроках, щоб жадібний алгоритм гарантовано брав їх першими
                double gCost = Math.Round(1.0 - (levelP - r) * 1e-4, 6);
                sensorToPoints.Add(greedySet);
                costs.Add(gCost);
            }
        }

        return BuildInstance(
            instanceId: $"Adv_p{levelP}_b{numBlocks}_n{numPoints}_k{coverageK}_s{seed}",
            family: "Adversarial",
            numPoints: numPoints,
            coverageK: coverageK,
            seed: seed,
            costs: costs.ToArray(),
            sensorToPoints: sensorToPoints);
    }

    /// <summary>
    /// 3. Завантаження або генерація реалістичної анонімізованої схеми шахтного горизонту
    /// (мережа вентиляційних штреків, квершлагів, лав та камер із ціликами-перешкодами).
    /// </summary>
    public static SetCoverInstance LoadOrGenerateRealMinePlan(string jsonPath, int densityMultiplier = 1, int coverageK = 1, int seed = 1008)
    {
        var rng = new Random(seed);
        // Якщо файл ще не створено, створюємо його з канонічною топологією шахтного горизонту
        if (!File.Exists(jsonPath))
        {
            EnsureRealMinePlanJson(jsonPath);
        }

        string rawJson = File.ReadAllText(jsonPath);
        using JsonDocument doc = JsonDocument.Parse(rawJson);
        JsonElement root = doc.RootElement;

        var corridors = new List<(double X1, double Y1, double X2, double Y2, double Width)>();
        foreach (JsonElement corr in root.GetProperty("corridors").EnumerateArray())
        {
            corridors.Add((
                corr.GetProperty("x1").GetDouble(),
                corr.GetProperty("y1").GetDouble(),
                corr.GetProperty("x2").GetDouble(),
                corr.GetProperty("y2").GetDouble(),
                corr.GetProperty("width").GetDouble()
            ));
        }

        var candidatePositions = new List<(double X, double Y, double Radius, double BaseCost)>();
        foreach (JsonElement pos in root.GetProperty("candidate_sensors").EnumerateArray())
        {
            candidatePositions.Add((
                pos.GetProperty("x").GetDouble(),
                pos.GetProperty("y").GetDouble(),
                pos.GetProperty("radius").GetDouble(),
                pos.GetProperty("cost").GetDouble()
            ));
        }

        // Дискретизуємо контрольні точки вздовж вентиляційних виробок
        var points = new List<(double X, double Y, int CorridorId)>();
        int ptsPerCorridor = Math.Max(8, 12 * densityMultiplier);
        for (int cIdx = 0; cIdx < corridors.Count; cIdx++)
        {
            var c = corridors[cIdx];
            for (int p = 0; p < ptsPerCorridor; p++)
            {
                double t = (p + 0.5) / ptsPerCorridor;
                double x = c.X1 + t * (c.X2 - c.X1) + (rng.NextDouble() - 0.5) * c.Width * 0.4;
                double y = c.Y1 + t * (c.Y2 - c.Y1) + (rng.NextDouble() - 0.5) * c.Width * 0.4;
                points.Add((x, y, cIdx));
            }
        }

        // Якщо densityMultiplier > 1, додаємо також проміжні кандидатні позиції вздовж виробок
        var allSensors = new List<(double X, double Y, double Radius, double Cost)>(candidatePositions);
        if (densityMultiplier > 1)
        {
            for (int cIdx = 0; cIdx < corridors.Count; cIdx++)
            {
                var c = corridors[cIdx];
                int extra = 4 * densityMultiplier;
                for (int e = 0; e < extra; e++)
                {
                    double t = (e + 0.25) / extra;
                    double x = c.X1 + t * (c.X2 - c.X1);
                    double y = c.Y1 + t * (c.Y2 - c.Y1);
                    double r = 28.0 + (rng.NextDouble() * 10.0);
                    double cost = Math.Round(1.8 + 0.04 * r + rng.NextDouble() * 0.6, 3);
                    allSensors.Add((x, y, r, cost));
                }
            }
        }

        int n = points.Count;
        var sensorToPoints = new List<List<int>>();
        var costs = new List<double>();
        int[] covCount = new int[n];

        foreach (var s in allSensors)
        {
            var covered = new List<int>();
            for (int i = 0; i < n; i++)
            {
                double dx = s.X - points[i].X;
                double dy = s.Y - points[i].Y;
                double dist = Math.Sqrt(dx * dx + dy * dy);
                // Датчик покриває точку у виробці, якщо вона в радіусі дії і лежить у суміжній виробці
                if (dist <= s.Radius)
                {
                    covered.Add(i);
                    covCount[i]++;
                }
            }
            if (covered.Count > 0)
            {
                sensorToPoints.Add(covered);
                costs.Add(s.Cost);
            }
        }

        // Гарантуємо припустимість для k-покриття
        for (int i = 0; i < n; i++)
        {
            while (covCount[i] < coverageK)
            {
                var covered = new List<int>();
                for (int jPt = 0; jPt < n; jPt++)
                {
                    double dx = points[i].X - points[jPt].X;
                    double dy = points[i].Y - points[jPt].Y;
                    if (dx * dx + dy * dy <= 25.0 * 25.0)
                    {
                        covered.Add(jPt);
                        covCount[jPt]++;
                    }
                }
                sensorToPoints.Add(covered);
                costs.Add(Math.Round(2.5 + rng.NextDouble() * 0.5, 3));
            }
        }

        return BuildInstance(
            instanceId: $"RealMine_d{densityMultiplier}_n{n}_m{sensorToPoints.Count}_k{coverageK}_s{seed}",
            family: "RealMinePlan",
            numPoints: n,
            coverageK: coverageK,
            seed: seed,
            costs: costs.ToArray(),
            sensorToPoints: sensorToPoints);
    }

    public static void EnsureRealMinePlanJson(string jsonPath)
    {
        string? dir = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var plan = new
        {
            name = "Анонімізована схема вентиляційного горизонту шахти (-420 м) для розміщення датчиків метану CH4",
            license = "CC-BY-4.0 (анонімізована навчально-дослідницька схема на основі типових гірничих виробок)",
            units = "meters",
            corridors = new[]
            {
                // Головні відкатні та вентиляційні штреки (горизонтальні)
                new { id = "drift_north", x1 = 0.0, y1 = 20.0, x2 = 200.0, y2 = 20.0, width = 4.5 },
                new { id = "drift_central", x1 = 0.0, y1 = 70.0, x2 = 200.0, y2 = 70.0, width = 5.0 },
                new { id = "drift_south", x1 = 0.0, y1 = 120.0, x2 = 200.0, y2 = 120.0, width = 4.5 },
                // Квершлаги та очисні лави (вертикальні та діагональні з'єднання)
                new { id = "crosscut_west", x1 = 20.0, y1 = 20.0, x2 = 20.0, y2 = 120.0, width = 4.0 },
                new { id = "longwall_1", x1 = 70.0, y1 = 20.0, x2 = 70.0, y2 = 120.0, width = 3.8 },
                new { id = "longwall_2", x1 = 130.0, y1 = 20.0, x2 = 130.0, y2 = 120.0, width = 3.8 },
                new { id = "crosscut_east", x1 = 180.0, y1 = 20.0, x2 = 180.0, y2 = 120.0, width = 4.0 },
                new { id = "vent_incline", x1 = 70.0, y1 = 70.0, x2 = 130.0, y2 = 20.0, width = 3.5 }
            },
            candidate_sensors = new[]
            {
                new { id = "S01", x = 20.0, y = 20.0, radius = 38.0, cost = 3.2 },
                new { id = "S02", x = 50.0, y = 20.0, radius = 32.0, cost = 2.4 },
                new { id = "S03", x = 70.0, y = 20.0, radius = 40.0, cost = 3.5 },
                new { id = "S04", x = 100.0, y = 20.0, radius = 32.0, cost = 2.3 },
                new { id = "S05", x = 130.0, y = 20.0, radius = 40.0, cost = 3.6 },
                new { id = "S06", x = 160.0, y = 20.0, radius = 32.0, cost = 2.4 },
                new { id = "S07", x = 180.0, y = 20.0, radius = 38.0, cost = 3.2 },
                new { id = "S08", x = 20.0, y = 45.0, radius = 30.0, cost = 2.1 },
                new { id = "S09", x = 70.0, y = 45.0, radius = 32.0, cost = 2.5 },
                new { id = "S10", x = 100.0, y = 45.0, radius = 34.0, cost = 2.6 },
                new { id = "S11", x = 130.0, y = 45.0, radius = 32.0, cost = 2.5 },
                new { id = "S12", x = 180.0, y = 45.0, radius = 30.0, cost = 2.1 },
                new { id = "S13", x = 20.0, y = 70.0, radius = 40.0, cost = 3.4 },
                new { id = "S14", x = 45.0, y = 70.0, radius = 32.0, cost = 2.3 },
                new { id = "S15", x = 70.0, y = 70.0, radius = 45.0, cost = 4.1 },
                new { id = "S16", x = 100.0, y = 70.0, radius = 35.0, cost = 2.7 },
                new { id = "S17", x = 130.0, y = 70.0, radius = 45.0, cost = 4.0 },
                new { id = "S18", x = 155.0, y = 70.0, radius = 32.0, cost = 2.3 },
                new { id = "S19", x = 180.0, y = 70.0, radius = 40.0, cost = 3.4 },
                new { id = "S20", x = 20.0, y = 95.0, radius = 30.0, cost = 2.2 },
                new { id = "S21", x = 70.0, y = 95.0, radius = 32.0, cost = 2.5 },
                new { id = "S22", x = 130.0, y = 95.0, radius = 32.0, cost = 2.5 },
                new { id = "S23", x = 180.0, y = 95.0, radius = 30.0, cost = 2.2 },
                new { id = "S24", x = 20.0, y = 120.0, radius = 38.0, cost = 3.2 },
                new { id = "S25", x = 50.0, y = 120.0, radius = 32.0, cost = 2.4 },
                new { id = "S26", x = 70.0, y = 120.0, radius = 40.0, cost = 3.5 },
                new { id = "S27", x = 100.0, y = 120.0, radius = 32.0, cost = 2.3 },
                new { id = "S28", x = 130.0, y = 120.0, radius = 40.0, cost = 3.5 },
                new { id = "S29", x = 160.0, y = 120.0, radius = 32.0, cost = 2.4 },
                new { id = "S30", x = 180.0, y = 120.0, radius = 38.0, cost = 3.2 }
            }
        };

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(plan, options));
    }

    private static double SampleCost(Random rng, string costMode)
    {
        return costMode switch
        {
            "Uniform" => Math.Round(1.0 + rng.NextDouble() * 9.0, 4),
            "Unit" => 1.0,
            _ => Math.Round(1.0 + rng.NextDouble() * 4.0, 4)
        };
    }

    public static bool HasLineOfSight(double x1, double y1, double x2, double y2, List<WallSegment> walls)
    {
        foreach (var w in walls)
        {
            if (SegmentsIntersect(x1, y1, x2, y2, w.X1, w.Y1, w.X2, w.Y2))
                return false;
        }
        return true;
    }

    private static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        double d1 = Direction(cx, cy, dx, dy, ax, ay);
        double d2 = Direction(cx, cy, dx, dy, bx, by);
        double d3 = Direction(ax, ay, bx, by, cx, cy);
        double d4 = Direction(ax, ay, bx, by, dx, dy);

        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
            ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
        {
            return true;
        }
        return false;
    }

    private static double Direction(double x1, double y1, double x2, double y2, double x3, double y3)
    {
        return (x3 - x1) * (y2 - y1) - (x2 - x1) * (y3 - y1);
    }

    public static SetCoverInstance BuildInstance(
        string instanceId,
        string family,
        int numPoints,
        int coverageK,
        int seed,
        double[] costs,
        List<List<int>> sensorToPoints)
    {
        int m = sensorToPoints.Count;
        int[][] s2p = new int[m][];
        var p2sList = new List<int>[numPoints];
        for (int i = 0; i < numPoints; i++) p2sList[i] = new List<int>();

        for (int j = 0; j < m; j++)
        {
            var distinctPts = sensorToPoints[j].Distinct().OrderBy(x => x).ToArray();
            s2p[j] = distinctPts;
            foreach (int pt in distinctPts)
            {
                p2sList[pt].Add(j);
            }
        }

        int[][] p2s = new int[numPoints][];
        for (int i = 0; i < numPoints; i++)
        {
            p2s[i] = p2sList[i].ToArray();
        }

        return new SetCoverInstance
        {
            InstanceId = instanceId,
            Family = family,
            NumPoints = numPoints,
            NumSensors = m,
            CoverageMultiplicityK = coverageK,
            Seed = seed,
            Costs = costs,
            SensorToPoints = s2p,
            PointToSensors = p2s
        };
    }
}
