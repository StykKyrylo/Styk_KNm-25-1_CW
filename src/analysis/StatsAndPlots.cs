using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KursovaKP08.Analysis;

public sealed record InstanceSummaryRow(
    string InstanceId,
    string Family,
    int NumPoints,
    int NumSensors,
    int CoverageK,
    int Seed,
    int MaxSetSizeD,
    int MaxFreqF,
    double HarmonicBoundHd,
    double LpLowerBound,
    double? ExactOptCost,
    string MethodName,
    double MedianCost,
    double IqrCost,
    double MedianRatioToOptOrLp,
    double MedianSetupMs,
    double MedianSolveMs,
    double MedianTotalMs,
    double IqrTotalMs,
    long PeakMemoryBytes,
    double FeasibleRate);

/// <summary>
/// Статистичний аналіз експериментальних даних (розділ 5.5 та Додаток Д методичних вказівок):
/// - Медіана, міжквартильний розмах (IQR), мінімум/максимум;
/// - Довірчий інтервал медіани бутстрепом (10 000 перевибірок, 95% ДІ);
/// - Парний тест Вілкоксона для знакових рангів (Wilcoxon signed-rank test);
/// - Непараметричний розмір ефекту Варга–Делейні A12;
/// - Поправка Холма (Holm-Bonferroni) для множинних порівнянь;
/// - Регресія в подвійних логарифмічних осях (log-log slope) з 95% ДІ;
/// - Генерація векторних графіків SVG та Markdown-таблиць із сирих CSV.
/// </summary>
public static class StatsAndPlots
{
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToArray();
        int n = sorted.Length;
        return (n % 2 == 1) ? sorted[n / 2] : 0.5 * (sorted[n / 2 - 1] + sorted[n / 2]);
    }

    public static double Quantile(IReadOnlyList<double> sortedValues, double q)
    {
        if (sortedValues.Count == 0) return 0.0;
        if (sortedValues.Count == 1) return sortedValues[0];
        double pos = q * (sortedValues.Count - 1);
        int lo = (int)Math.Floor(pos);
        int hi = (int)Math.Ceiling(pos);
        if (lo == hi) return sortedValues[lo];
        return sortedValues[lo] + (pos - lo) * (sortedValues[hi] - sortedValues[lo]);
    }

    public static double Iqr(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return 0.0;
        var sorted = values.OrderBy(v => v).ToArray();
        return Quantile(sorted, 0.75) - Quantile(sorted, 0.25);
    }

    /// <summary>
    /// 95% довірчий інтервал медіани методом бутстрепу (10 000 перевибірок, Додаток Д).
    /// </summary>
    public static (double Lo, double Hi) BootstrapMedianCi(
        IReadOnlyList<double> values,
        int nBoot = 10_000,
        double alpha = 0.05,
        int seed = 0)
    {
        int n = values.Count;
        if (n == 0) return (0.0, 0.0);
        if (n == 1) return (values[0], values[0]);

        var rng = new Random(seed);
        double[] boots = new double[nBoot];
        double[] sample = new double[n];

        for (int b = 0; b < nBoot; b++)
        {
            for (int i = 0; i < n; i++)
            {
                sample[i] = values[rng.Next(n)];
            }
            Array.Sort(sample);
            boots[b] = (n % 2 == 1) ? sample[n / 2] : 0.5 * (sample[n / 2 - 1] + sample[n / 2]);
        }

        Array.Sort(boots);
        double lo = Quantile(boots, alpha / 2.0);
        double hi = Quantile(boots, 1.0 - alpha / 2.0);
        return (lo, hi);
    }

    /// <summary>
    /// Розмір ефекту Варга–Делейні A12: P(A > B) + 0.5 * P(A == B).
    /// 0.5 — немає ефекту; 1.0 — A завжди більше за B; 0.0 — A завжди менше за B.
    /// </summary>
    public static double VarghaDelaneyA12(IReadOnlyList<double> a, IReadOnlyList<double> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.5;
        double greater = 0.0;
        double ties = 0.0;
        for (int i = 0; i < a.Count; i++)
        {
            for (int j = 0; j < b.Count; j++)
            {
                double diff = a[i] - b[j];
                if (Math.Abs(diff) <= 1e-12) ties += 1.0;
                else if (diff > 0) greater += 1.0;
            }
        }
        return (greater + 0.5 * ties) / ((double)a.Count * b.Count);
    }

    /// <summary>
    /// Парний тест Вілкоксона для знакових рангів (Wilcoxon signed-rank test) із поправкою на зв'язки (ties).
    /// Повертає статистику W+ та двостороннє p-значення.
    /// </summary>
    public static (double StatisticW, double PValue) WilcoxonSignedRank(
        IReadOnlyList<double> a,
        IReadOnlyList<double> b)
    {
        int n = Math.Min(a.Count, b.Count);
        var diffs = new List<(double AbsDiff, int Sign)>();
        for (int i = 0; i < n; i++)
        {
            double d = a[i] - b[i];
            if (Math.Abs(d) > 1e-12)
            {
                diffs.Add((Math.Abs(d), d > 0 ? 1 : -1));
            }
        }

        int nr = diffs.Count;
        if (nr == 0) return (0.0, 1.0);

        diffs.Sort((x, y) => x.AbsDiff.CompareTo(y.AbsDiff));

        double[] ranks = new double[nr];
        double tieSumCubes = 0.0;
        int idx = 0;
        while (idx < nr)
        {
            int end = idx;
            while (end + 1 < nr && Math.Abs(diffs[end + 1].AbsDiff - diffs[idx].AbsDiff) <= 1e-12)
                end++;
            double avgRank = 0.5 * ((idx + 1) + (end + 1));
            int tieCount = end - idx + 1;
            if (tieCount > 1)
                tieSumCubes += (Math.Pow(tieCount, 3) - tieCount);
            for (int k = idx; k <= end; k++)
                ranks[k] = avgRank;
            idx = end + 1;
        }

        double wPlus = 0.0;
        double wMinus = 0.0;
        for (int i = 0; i < nr; i++)
        {
            if (diffs[i].Sign > 0) wPlus += ranks[i];
            else wMinus += ranks[i];
        }

        double wStat = Math.Min(wPlus, wMinus);
        double mean = nr * (nr + 1) / 4.0;
        double variance = (nr * (nr + 1) * (2 * nr + 1) / 24.0) - (tieSumCubes / 48.0);
        if (variance <= 1e-12) return (wStat, 1.0);

        double z = (Math.Abs(wPlus - mean) - 0.5) / Math.Sqrt(variance);
        double pVal = 2.0 * (1.0 - NormalCdf(Math.Max(0.0, z)));
        return (wStat, Math.Clamp(pVal, 0.0, 1.0));
    }

    /// <summary>
    /// Поправка Холма (Holm-Bonferroni) для множинних гіпотез (Додаток Д).
    /// </summary>
    public static double[] HolmAdjust(IReadOnlyList<double> pValues)
    {
        int m = pValues.Count;
        if (m == 0) return Array.Empty<double>();

        int[] order = Enumerable.Range(0, m).OrderBy(i => pValues[i]).ToArray();
        double[] adj = new double[m];
        double running = 0.0;

        for (int rank = 0; rank < m; rank++)
        {
            int origIdx = order[rank];
            double val = Math.Min(1.0, (m - rank) * pValues[origIdx]);
            running = Math.Max(running, val);
            adj[origIdx] = running;
        }
        return adj;
    }

    /// <summary>
    /// Лінійна регресія в подвійних логарифмічних осях log10(y) = a + slope * log10(x) з 95% ДІ нахилу.
    /// </summary>
    public static (double Slope, double SlopeCiLo, double SlopeCiHi, double R2) LogLogRegression(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y)
    {
        int n = Math.Min(x.Count, y.Count);
        if (n < 2) return (0, 0, 0, 0);

        double[] lx = new double[n];
        double[] ly = new double[n];
        for (int i = 0; i < n; i++)
        {
            lx[i] = Math.Log10(Math.Max(1e-9, x[i]));
            ly[i] = Math.Log10(Math.Max(1e-9, y[i]));
        }

        double mx = lx.Average();
        double my = ly.Average();
        double sxx = 0.0, sxy = 0.0, syy = 0.0;
        for (int i = 0; i < n; i++)
        {
            double dx = lx[i] - mx;
            double dy = ly[i] - my;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }

        if (sxx <= 1e-12) return (0, 0, 0, 0);
        double slope = sxy / sxx;
        double intercept = my - slope * mx;
        double r2 = syy > 1e-12 ? (sxy * sxy) / (sxx * syy) : 1.0;

        double sse = 0.0;
        for (int i = 0; i < n; i++)
        {
            double pred = intercept + slope * lx[i];
            double err = ly[i] - pred;
            sse += err * err;
        }

        double seSlope = n > 2 ? Math.Sqrt((sse / (n - 2)) / sxx) : 0.0;
        double tCrit = n > 10 ? 2.04 : 2.57;
        return (slope, slope - tCrit * seSlope, slope + tCrit * seSlope, r2);
    }

    private static double NormalCdf(double z)
    {
        // Наближення Абрамовіца–Стегана (похибка < 7.5e-8)
        double t = 1.0 / (1.0 + 0.2316419 * Math.Abs(z));
        double d = 0.3989422804014327 * Math.Exp(-z * z / 2.0);
        double p = d * t * (0.319381530 + t * (-0.356563782 + t * (1.781477937 + t * (-1.821255978 + t * 1.330274429))));
        return z >= 0 ? 1.0 - p : p;
    }

    /// <summary>
    /// Генерує всі підсумкові таблиці (Markdown) та векторні графіки (SVG) із файлу instance_summary.csv.
    /// </summary>
    public static void GenerateFullReportAndFigures(
        IReadOnlyList<InstanceSummaryRow> rows,
        string resultsDir,
        int bootstrapSeed)
    {
        string figuresDir = Path.Combine(resultsDir, "figures");
        Directory.CreateDirectory(figuresDir);

        GenerateSvgBoxplotRatios(rows, Path.Combine(figuresDir, "fig1_approx_ratio_boxplot.svg"));
        GenerateSvgScalingLogLog(rows, Path.Combine(figuresDir, "fig2_scaling_loglog.svg"));
        GenerateSvgEcdf(rows, Path.Combine(figuresDir, "fig3_ecdf_ratios.svg"));
        GenerateSvgKCoverage(rows, Path.Combine(figuresDir, "fig4_k_coverage_comparison.svg"));

        string summaryMdPath = Path.Combine(resultsDir, "summary_tables.md");
        WriteSummaryMarkdown(rows, summaryMdPath, bootstrapSeed);
    }

    private static void WriteSummaryMarkdown(IReadOnlyList<InstanceSummaryRow> rows, string outputPath, int seed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Зведені результати обчислювального експерименту (КР-08)");
        sb.AppendLine();
        sb.AppendLine("> Файл згенеровано автоматично скриптом аналізу з первинних CSV-вимірювань (`results/raw/instance_summary.csv`).");
        sb.AppendLine();

        // 1. Зведена таблиця по сімействах задач (k=1)
        sb.AppendLine("## Таблиця 1. Якість апроксимації (відношення Cost / LP*) та час розв'язування за сімействами задач (k = 1)");
        sb.AppendLine();
        sb.AppendLine("| Сімейство екземплярів | Метод | К-сть екз. N | Медіана Cost/LP* | 95% ДІ медіани (бутстреп) | IQR Cost/LP* | Макс. Cost/LP* | Теор. межа (медіана) | Медіана часу (мс) | IQR часу (мс) |");
        sb.AppendLine("|---|---|---:|---:|---|---:|---:|---:|---:|---:|");

        var families = new[] { "Plane", "MineObstacles", "Adversarial", "RealMinePlan" };
        var methods = new[] { "Greedy", "LP-Threshold", "LP-Randomized", "Exact-MILP" };

        foreach (string fam in families)
        {
            foreach (string m in methods)
            {
                var sub = rows.Where(r => r.Family == fam && r.CoverageK == 1 && r.MethodName == m && r.FeasibleRate > 0.5).ToList();
                if (sub.Count == 0) continue;

                var ratios = sub.Select(r => r.MedianRatioToOptOrLp).ToList();
                var times = sub.Select(r => r.MedianTotalMs).ToList();
                var bounds = sub.Select(r => m == "Greedy" ? r.HarmonicBoundHd : (double)r.MaxFreqF).ToList();

                double medRatio = Median(ratios);
                var (ciLo, ciHi) = BootstrapMedianCi(ratios, 10_000, 0.05, seed);
                double iqrRatio = Iqr(ratios);
                double maxRatio = ratios.Max();
                double medBound = Median(bounds);
                double medTime = Median(times);
                double iqrTime = Iqr(times);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2} | {3:F4} | [{4:F4}; {5:F4}] | {6:F4} | {7:F4} | {8:F2} | {9:F3} | {10:F3} |",
                    fam, m, sub.Count, medRatio, ciLo, ciHi, iqrRatio, maxRatio, medBound, medTime, iqrTime));
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Таблиця 2. Статистичне порівняння методів (парний тест Вілкоксона, розмір ефекту Варга–Делейні A12, поправка Холма)");
        sb.AppendLine();
        sb.AppendLine("| Сімейство (k=1) | Пара методів (A vs B) | Метрика | К-сть пар N | Медіана A | Медіана B | Відношення медіан A/B | Статистика W | p-значення (сире) | p-значення (Холм) | Розмір ефекту A12 |");
        sb.AppendLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|");

        var comparisons = new List<(string Fam, string MA, string MB, string Metric, int Count, double MedA, double MedB, double W, double P, double A12)>();

        foreach (string fam in families)
        {
            var byInst = rows.Where(r => r.Family == fam && r.CoverageK == 1)
                             .GroupBy(r => r.InstanceId)
                             .ToList();

            var pairs = new (string MA, string MB)[]
            {
                ("Greedy", "LP-Threshold"),
                ("Greedy", "LP-Randomized")
            };

            foreach (var (mA, mB) in pairs)
            {
                var listA = new List<double>();
                var listB = new List<double>();
                foreach (var g in byInst)
                {
                    var rA = g.FirstOrDefault(x => x.MethodName == mA);
                    var rB = g.FirstOrDefault(x => x.MethodName == mB);
                    if (rA != null && rB != null)
                    {
                        listA.Add(rA.MedianRatioToOptOrLp);
                        listB.Add(rB.MedianRatioToOptOrLp);
                    }
                }
                if (listA.Count >= 3)
                {
                    var (w, p) = WilcoxonSignedRank(listA, listB);
                    double a12 = VarghaDelaneyA12(listA, listB);
                    comparisons.Add((fam, mA, mB, "Cost/LP*", listA.Count, Median(listA), Median(listB), w, p, a12));
                }
            }
        }

        double[] holmP = HolmAdjust(comparisons.Select(c => c.P).ToList());
        for (int i = 0; i < comparisons.Count; i++)
        {
            var c = comparisons[i];
            double ratioAB = c.MedB > 1e-9 ? c.MedA / c.MedB : 1.0;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1} vs {2} | {3} | {4} | {5:F4} | {6:F4} | {7:F4} | {8:F1} | {9:E3} | {10:E3} | {11:F3} |",
                c.Fam, c.MA, c.MB, c.Metric, c.Count, c.MedA, c.MedB, ratioAB, c.W, c.P, holmP[i], c.A12));
        }

        sb.AppendLine();
        sb.AppendLine("## Таблиця 3. Масштабування часу виконання за розмірністю n (регресія в логарифмічних осях)");
        sb.AppendLine();
        sb.AppendLine("| Метод | Діапазон n | Емпіричний показник степеня beta | 95% ДІ нахилу [beta_lo; beta_hi] | Коефіцієнт детермінації R^2 |");
        sb.AppendLine("|---|---|---:|---|---:|");

        foreach (string m in methods)
        {
            var sub = rows.Where(r => (r.Family == "Plane" || r.Family == "MineObstacles") && r.CoverageK == 1 && r.MethodName == m && r.FeasibleRate > 0.5)
                          .OrderBy(r => r.NumPoints)
                          .ToList();
            if (sub.Count >= 3)
            {
                var xs = sub.Select(r => (double)r.NumPoints).ToList();
                var ys = sub.Select(r => Math.Max(0.001, r.MedianTotalMs)).ToList();
                var (slope, lo, hi, r2) = LogLogRegression(xs, ys);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} .. {2} | {3:F3} | [{4:F3}; {5:F3}] | {6:F4} |",
                    m, sub.Min(r => r.NumPoints), sub.Max(r => r.NumPoints), slope, lo, hi, r2));
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Таблиця 4. Вплив кратності резервування k in {1, 2, 3} (сімейство MineObstacles)");
        sb.AppendLine();
        sb.AppendLine("| Кратність k | Метод | Медіана Cost/LP* | IQR Cost/LP* | Медіана часу (мс) |");
        sb.AppendLine("|---:|---|---:|---:|---:|");

        for (int kVal = 1; kVal <= 3; kVal++)
        {
            foreach (string m in new[] { "Greedy", "LP-Threshold", "LP-Randomized" })
            {
                var sub = rows.Where(r => r.Family == "MineObstacles" && r.CoverageK == kVal && r.MethodName == m).ToList();
                if (sub.Count == 0) continue;
                var ratios = sub.Select(r => r.MedianRatioToOptOrLp).ToList();
                var times = sub.Select(r => r.MedianTotalMs).ToList();
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "| {0} | {1} | {2:F4} | {3:F4} | {4:F3} |",
                    kVal, m, Median(ratios), Iqr(ratios), Median(times)));
            }
        }

        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSvgBoxplotRatios(IReadOnlyList<InstanceSummaryRow> rows, string svgPath)
    {
        int width = 880, height = 460;
        int padL = 85, padR = 40, padT = 55, padB = 85;
        int plotW = width - padL - padR;
        int plotH = height - padT - padB;

        var families = new[] { "Plane", "MineObstacles", "Adversarial", "RealMinePlan" };
        var methods = new[] { "Greedy", "LP-Threshold", "LP-Randomized" };
        var colors = new Dictionary<string, string>
        {
            ["Greedy"] = "#1f77b4",
            ["LP-Threshold"] = "#ff7f0e",
            ["LP-Randomized"] = "#2ca02c"
        };

        double minY = 1.0;
        double maxY = Math.Max(2.6, rows.Where(r => r.CoverageK == 1).Select(r => r.MedianRatioToOptOrLp).DefaultIfEmpty(2.5).Max() * 1.08);

        double MapY(double val) => padT + plotH - (Math.Clamp(val - minY, 0, maxY - minY) / (maxY - minY)) * plotH;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}' width='{width}' height='{height}' font-family='Segoe UI, Arial, sans-serif'>");
        sb.AppendLine($"<rect width='{width}' height='{height}' fill='#ffffff'/>");
        sb.AppendLine($"<text x='{width / 2}' y='28' text-anchor='middle' font-size='15' font-weight='bold' fill='#111'>Рис. 1. Емпіричне відношення вартості до нижньої межі (Cost / LP*) за сімействами задач (k = 1)</text>");

        // Grid lines
        int ticks = 6;
        for (int t = 0; t <= ticks; t++)
        {
            double v = minY + t * (maxY - minY) / ticks;
            double y = MapY(v);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0}' y1='{1:F1}' x2='{2}' y2='{1:F1}' stroke='#e5e7eb' stroke-dasharray='4,4'/>",
                padL, y, width - padR));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1:F1}' text-anchor='end' font-size='12' fill='#374151'>{2:F2}</text>",
                padL - 8, y + 4, v));
        }

        sb.AppendLine($"<line x1='{padL}' y1='{padT}' x2='{padL}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<line x1='{padL}' y1='{padT + plotH}' x2='{width - padR}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<text x='22' y='{padT + plotH / 2}' transform='rotate(-90 22,{padT + plotH / 2})' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Відношення якості Cost / LP* (безрозм.)</text>");

        double groupW = (double)plotW / families.Length;
        for (int fIdx = 0; fIdx < families.Length; fIdx++)
        {
            string fam = families[fIdx];
            double groupX = padL + fIdx * groupW;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0:F1}' y='{1}' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>{2}</text>",
                groupX + groupW / 2, padT + plotH + 28, fam));

            for (int mIdx = 0; mIdx < methods.Length; mIdx++)
            {
                string m = methods[mIdx];
                var vals = rows.Where(r => r.Family == fam && r.CoverageK == 1 && r.MethodName == m)
                               .Select(r => r.MedianRatioToOptOrLp)
                               .OrderBy(v => v)
                               .ToList();
                if (vals.Count == 0) continue;

                double q25 = Quantile(vals, 0.25);
                double med = Quantile(vals, 0.50);
                double q75 = Quantile(vals, 0.75);
                double vmin = vals.First();
                double vmax = vals.Last();

                double cx = groupX + (mIdx + 0.8) * (groupW / (methods.Length + 0.6));
                double boxW = 28.0;
                string col = colors[m];

                double yMin = MapY(vmin);
                double yMax = MapY(vmax);
                double y25 = MapY(q25);
                double y75 = MapY(q75);
                double yMed = MapY(med);

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<line x1='{0:F1}' y1='{1:F1}' x2='{0:F1}' y2='{2:F1}' stroke='{3}' stroke-width='1.5'/>",
                    cx, yMax, yMin, col));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' fill='{4}' fill-opacity='0.25' stroke='{4}' stroke-width='1.8'/>",
                    cx - boxW / 2, Math.Min(y75, y25), boxW, Math.Max(2.0, Math.Abs(y25 - y75)), col));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<line x1='{0:F1}' y1='{1:F1}' x2='{2:F1}' y2='{1:F1}' stroke='{3}' stroke-width='2.8'/>",
                    cx - boxW / 2, yMed, cx + boxW / 2, col));

                for (int pIdx = 0; pIdx < vals.Count; pIdx++)
                {
                    double jitter = ((pIdx % 5) - 2) * 3.2;
                    double py = MapY(vals[pIdx]);
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "<circle cx='{0:F1}' cy='{1:F1}' r='2.6' fill='{2}' fill-opacity='0.75'/>",
                        cx + jitter, py, col));
                }
            }
        }

        // Legend
        int legX = padL + 20, legY = height - 28;
        foreach (var m in methods)
        {
            string col = colors[m];
            sb.AppendLine($"<rect x='{legX}' y='{legY - 11}' width='16' height='12' fill='{col}' fill-opacity='0.4' stroke='{col}' stroke-width='1.5'/>");
            sb.AppendLine($"<text x='{legX + 22}' y='{legY}' font-size='12' fill='#111'>{m}</text>");
            legX += 165;
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(svgPath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSvgScalingLogLog(IReadOnlyList<InstanceSummaryRow> rows, string svgPath)
    {
        int width = 880, height = 460;
        int padL = 85, padR = 40, padT = 55, padB = 85;
        int plotW = width - padL - padR;
        int plotH = height - padT - padB;

        var methods = new[] { "Greedy", "LP-Threshold", "LP-Randomized", "Exact-MILP" };
        var colors = new Dictionary<string, string>
        {
            ["Greedy"] = "#1f77b4",
            ["LP-Threshold"] = "#ff7f0e",
            ["LP-Randomized"] = "#2ca02c",
            ["Exact-MILP"] = "#d62728"
        };
        var dashes = new Dictionary<string, string>
        {
            ["Greedy"] = "none",
            ["LP-Threshold"] = "6,3",
            ["LP-Randomized"] = "2,2",
            ["Exact-MILP"] = "8,3,2,3"
        };

        double logMinX = 2.0, logMaxX = 5.0; // 10^2 .. 10^5
        double logMinY = -2.0, logMaxY = 4.5; // 0.01 ms .. 30,000 ms

        double MapX(double n) => padL + ((Math.Log10(Math.Max(100, n)) - logMinX) / (logMaxX - logMinX)) * plotW;
        double MapY(double ms) => padT + plotH - ((Math.Clamp(Math.Log10(Math.Max(0.01, ms)), logMinY, logMaxY) - logMinY) / (logMaxY - logMinY)) * plotH;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}' width='{width}' height='{height}' font-family='Segoe UI, Arial, sans-serif'>");
        sb.AppendLine($"<rect width='{width}' height='{height}' fill='#ffffff'/>");
        sb.AppendLine($"<text x='{width / 2}' y='28' text-anchor='middle' font-size='15' font-weight='bold' fill='#111'>Рис. 2. Масштабування часу виконання від кількості точок n (подвійна логарифмічна шкала, 95% ДІ)</text>");

        for (int lx = 2; lx <= 5; lx++)
        {
            double x = MapX(Math.Pow(10, lx));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0:F1}' y1='{1}' x2='{0:F1}' y2='{2}' stroke='#e5e7eb' stroke-dasharray='4,4'/>",
                x, padT, padT + plotH));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0:F1}' y='{1}' text-anchor='middle' font-size='12' fill='#374151'>10^{2}</text>",
                x, padT + plotH + 24, lx));
        }

        for (int ly = -2; ly <= 4; ly++)
        {
            double y = MapY(Math.Pow(10, ly));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0}' y1='{1:F1}' x2='{2}' y2='{1:F1}' stroke='#e5e7eb' stroke-dasharray='4,4'/>",
                padL, y, width - padR));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1:F1}' text-anchor='end' font-size='12' fill='#374151'>10^{2}</text>",
                padL - 8, y + 4, ly));
        }

        sb.AppendLine($"<line x1='{padL}' y1='{padT}' x2='{padL}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<line x1='{padL}' y1='{padT + plotH}' x2='{width - padR}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<text x='{padL + plotW / 2}' y='{padT + plotH + 46}' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Кількість контрольних точок n = |U| (од.)</text>");
        sb.AppendLine($"<text x='22' y='{padT + plotH / 2}' transform='rotate(-90 22,{padT + plotH / 2})' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Медіанний час виконання (мс)</text>");

        foreach (string m in methods)
        {
            var grouped = rows.Where(r => (r.Family == "Plane" || r.Family == "MineObstacles") && r.CoverageK == 1 && r.MethodName == m && r.FeasibleRate > 0.5)
                              .GroupBy(r => r.NumPoints)
                              .OrderBy(g => g.Key)
                              .ToList();
            if (grouped.Count == 0) continue;

            var pts = new List<(double X, double YMed, double YLo, double YHi)>();
            foreach (var g in grouped)
            {
                var times = g.Select(x => Math.Max(0.01, x.MedianTotalMs)).ToList();
                double med = Median(times);
                var (lo, hi) = BootstrapMedianCi(times, 2000, 0.05, 42);
                pts.Add((MapX(g.Key), MapY(med), MapY(lo), MapY(hi)));
            }

            string col = colors[m];
            string dash = dashes[m];

            if (pts.Count >= 2)
            {
                var poly = new StringBuilder();
                foreach (var p in pts) poly.AppendFormat(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", p.X, p.YHi);
                for (int i = pts.Count - 1; i >= 0; i--) poly.AppendFormat(CultureInfo.InvariantCulture, "{0:F1},{1:F1} ", pts[i].X, pts[i].YLo);
                sb.AppendLine($"<polygon points='{poly}' fill='{col}' fill-opacity='0.15'/>");

                var linePts = string.Join(" ", pts.Select(p => string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", p.X, p.YMed)));
                string dashAttr = dash == "none" ? "" : $" stroke-dasharray='{dash}'";
                sb.AppendLine($"<polyline points='{linePts}' fill='none' stroke='{col}' stroke-width='2.4'{dashAttr}/>");
            }

            foreach (var p in pts)
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<circle cx='{0:F1}' cy='{1:F1}' r='4.0' fill='{2}'/>",
                    p.X, p.YMed, col));
            }
        }

        int legX = padL + 20, legY = height - 18;
        foreach (var m in methods)
        {
            string col = colors[m];
            sb.AppendLine($"<line x1='{legX}' y1='{legY - 5}' x2='{legX + 24}' y2='{legY - 5}' stroke='{col}' stroke-width='2.5'/>");
            sb.AppendLine($"<text x='{legX + 30}' y='{legY}' font-size='12' fill='#111'>{m}</text>");
            legX += 165;
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(svgPath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSvgEcdf(IReadOnlyList<InstanceSummaryRow> rows, string svgPath)
    {
        int width = 880, height = 440;
        int padL = 85, padR = 40, padT = 55, padB = 80;
        int plotW = width - padL - padR;
        int plotH = height - padT - padB;

        var methods = new[] { "Greedy", "LP-Threshold", "LP-Randomized" };
        var colors = new Dictionary<string, string>
        {
            ["Greedy"] = "#1f77b4",
            ["LP-Threshold"] = "#ff7f0e",
            ["LP-Randomized"] = "#2ca02c"
        };

        double minX = 1.0;
        double maxX = Math.Max(2.2, rows.Where(r => r.CoverageK == 1).Select(r => r.MedianRatioToOptOrLp).DefaultIfEmpty(2.0).Max() * 1.05);

        double MapX(double r) => padL + ((Math.Clamp(r, minX, maxX) - minX) / (maxX - minX)) * plotW;
        double MapY(double p) => padT + plotH - p * plotH;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}' width='{width}' height='{height}' font-family='Segoe UI, Arial, sans-serif'>");
        sb.AppendLine($"<rect width='{width}' height='{height}' fill='#ffffff'/>");
        sb.AppendLine($"<text x='{width / 2}' y='28' text-anchor='middle' font-size='15' font-weight='bold' fill='#111'>Рис. 3. Емпірична функція розподілу (ECDF) відношення Cost / LP* на всіх тестових екземплярах</text>");

        for (int i = 0; i <= 5; i++)
        {
            double p = i * 0.2;
            double y = MapY(p);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0}' y1='{1:F1}' x2='{2}' y2='{1:F1}' stroke='#e5e7eb' stroke-dasharray='4,4'/>",
                padL, y, width - padR));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1:F1}' text-anchor='end' font-size='12' fill='#374151'>{2:F1}</text>",
                padL - 8, y + 4, p));

            double rx = minX + p * (maxX - minX);
            double x = MapX(rx);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0:F1}' y='{1}' text-anchor='middle' font-size='12' fill='#374151'>{2:F2}</text>",
                x, padT + plotH + 22, rx));
        }

        sb.AppendLine($"<line x1='{padL}' y1='{padT}' x2='{padL}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<line x1='{padL}' y1='{padT + plotH}' x2='{width - padR}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<text x='{padL + plotW / 2}' y='{padT + plotH + 45}' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Відношення якості Cost / LP* (поріг tau)</text>");
        sb.AppendLine($"<text x='22' y='{padT + plotH / 2}' transform='rotate(-90 22,{padT + plotH / 2})' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Частка розв'язаних екземплярів P(Cost/LP* &lt;= tau)</text>");

        foreach (string m in methods)
        {
            var vals = rows.Where(r => r.CoverageK == 1 && r.MethodName == m)
                           .Select(r => r.MedianRatioToOptOrLp)
                           .OrderBy(v => v)
                           .ToList();
            if (vals.Count == 0) continue;

            var stepPts = new List<string>();
            stepPts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", MapX(minX), MapY(0)));
            for (int i = 0; i < vals.Count; i++)
            {
                double x = MapX(vals[i]);
                double yPrev = MapY((double)i / vals.Count);
                double yNext = MapY((double)(i + 1) / vals.Count);
                stepPts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", x, yPrev));
                stepPts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", x, yNext));
            }
            stepPts.Add(string.Format(CultureInfo.InvariantCulture, "{0:F1},{1:F1}", MapX(maxX), MapY(1.0)));

            sb.AppendLine($"<polyline points='{string.Join(" ", stepPts)}' fill='none' stroke='{colors[m]}' stroke-width='2.4'/>");
        }

        int legX = padL + 20, legY = height - 16;
        foreach (var m in methods)
        {
            string col = colors[m];
            sb.AppendLine($"<line x1='{legX}' y1='{legY - 5}' x2='{legX + 24}' y2='{legY - 5}' stroke='{col}' stroke-width='2.5'/>");
            sb.AppendLine($"<text x='{legX + 30}' y='{legY}' font-size='12' fill='#111'>{m}</text>");
            legX += 165;
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(svgPath, sb.ToString(), Encoding.UTF8);
    }

    private static void GenerateSvgKCoverage(IReadOnlyList<InstanceSummaryRow> rows, string svgPath)
    {
        int width = 880, height = 440;
        int padL = 85, padR = 40, padT = 55, padB = 80;
        int plotW = width - padL - padR;
        int plotH = height - padT - padB;

        var methods = new[] { "Greedy", "LP-Threshold", "LP-Randomized" };
        var colors = new Dictionary<string, string>
        {
            ["Greedy"] = "#1f77b4",
            ["LP-Threshold"] = "#ff7f0e",
            ["LP-Randomized"] = "#2ca02c"
        };

        double maxCost = Math.Max(50.0, rows.Where(r => r.Family == "MineObstacles").Select(r => r.MedianCost).DefaultIfEmpty(50.0).Max() * 1.15);
        double MapY(double c) => padT + plotH - (Math.Clamp(c, 0, maxCost) / maxCost) * plotH;

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 {width} {height}' width='{width}' height='{height}' font-family='Segoe UI, Arial, sans-serif'>");
        sb.AppendLine($"<rect width='{width}' height='{height}' fill='#ffffff'/>");
        sb.AppendLine($"<text x='{width / 2}' y='28' text-anchor='middle' font-size='15' font-weight='bold' fill='#111'>Рис. 4. Сумарна вартість покриття залежно від кратності резервування k in {{1, 2, 3}} (MineObstacles)</text>");

        for (int t = 0; t <= 5; t++)
        {
            double v = t * maxCost / 5.0;
            double y = MapY(v);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<line x1='{0}' y1='{1:F1}' x2='{2}' y2='{1:F1}' stroke='#e5e7eb' stroke-dasharray='4,4'/>",
                padL, y, width - padR));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1:F1}' text-anchor='end' font-size='12' fill='#374151'>{2:F1}</text>",
                padL - 8, y + 4, v));
        }

        sb.AppendLine($"<line x1='{padL}' y1='{padT}' x2='{padL}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<line x1='{padL}' y1='{padT + plotH}' x2='{width - padR}' y2='{padT + plotH}' stroke='#111' stroke-width='1.5'/>");
        sb.AppendLine($"<text x='22' y='{padT + plotH / 2}' transform='rotate(-90 22,{padT + plotH / 2})' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>Медіанна вартість покриття (ум. од.)</text>");

        double groupW = (double)plotW / 3.0;
        for (int kVal = 1; kVal <= 3; kVal++)
        {
            double gx = padL + (kVal - 1) * groupW;
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0:F1}' y='{1}' text-anchor='middle' font-size='13' font-weight='bold' fill='#111'>k = {2}</text>",
                gx + groupW / 2, padT + plotH + 26, kVal));

            for (int mIdx = 0; mIdx < methods.Length; mIdx++)
            {
                string m = methods[mIdx];
                var vals = rows.Where(r => r.Family == "MineObstacles" && r.CoverageK == kVal && r.MethodName == m)
                               .Select(r => r.MedianCost)
                               .ToList();
                if (vals.Count == 0) continue;

                double med = Median(vals);
                var (lo, hi) = BootstrapMedianCi(vals, 2000, 0.05, 42);
                double bx = gx + 35 + mIdx * 62;
                double bw = 44;
                double yTop = MapY(med);
                double hBar = (padT + plotH) - yTop;
                string col = colors[m];

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<rect x='{0:F1}' y='{1:F1}' width='{2:F1}' height='{3:F1}' fill='{4}' fill-opacity='0.65' stroke='{4}' stroke-width='1.5'/>",
                    bx, yTop, bw, Math.Max(1.0, hBar), col));

                double cx = bx + bw / 2;
                double yLo = MapY(lo), yHi = MapY(hi);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "<line x1='{0:F1}' y1='{1:F1}' x2='{0:F1}' y2='{2:F1}' stroke='#111' stroke-width='1.8'/>",
                    cx, yHi, yLo));
            }
        }

        int legX = padL + 20, legY = height - 16;
        foreach (var m in methods)
        {
            string col = colors[m];
            sb.AppendLine($"<rect x='{legX}' y='{legY - 11}' width='16' height='12' fill='{col}' fill-opacity='0.65'/>");
            sb.AppendLine($"<text x='{legX + 22}' y='{legY}' font-size='12' fill='#111'>{m}</text>");
            legX += 165;
        }

        sb.AppendLine("</svg>");
        File.WriteAllText(svgPath, sb.ToString(), Encoding.UTF8);
    }
}
