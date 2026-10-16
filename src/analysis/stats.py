# -*- coding: utf-8 -*-
"""
Додаток Д методичних вказівок: Бутстреп, парний тест Вілкоксона,
розмір ефекту Варга–Делейні A12, поправка Холма.
Може запускатися окремо поверх CSV із вимірюваннями (results/raw/instance_summary.csv).
"""

import numpy as np
from scipy import stats


def bootstrap_ci(x, stat=np.median, n_boot=10_000, alpha=0.05, seed=0):
    """Довірчий інтервал статистики бутстрепом."""
    rng = np.random.default_rng(seed)
    x = np.asarray(x, dtype=float)
    boots = np.array([stat(rng.choice(x, size=x.size, replace=True))
                      for _ in range(n_boot)])
    lo, hi = np.quantile(boots, [alpha / 2, 1 - alpha / 2])
    return float(lo), float(hi)


def vargha_delaney_a12(a, b):
    """P(a > b) + 0.5 * P(a == b). 0.5 — немає ефекту, 1.0 — a завжди більше."""
    a, b = np.asarray(a, dtype=float), np.asarray(b, dtype=float)
    ranks = stats.rankdata(np.concatenate([a, b]))
    r1 = ranks[: a.size].sum()
    return float((r1 / a.size - (a.size + 1) / 2) / b.size)


def holm_adjust(pvals):
    """Скориговані p-значення за методом Холма."""
    p = np.asarray(pvals, dtype=float)
    order = np.argsort(p)
    m = p.size
    adj = np.empty(m)
    running = 0.0
    for rank, idx in enumerate(order):
        running = max(running, min(1.0, (m - rank) * p[idx]))
        adj[idx] = running
    return adj


if __name__ == "__main__":
    # Перевірка еталонних значень із Додатка Д:
    assert abs(vargha_delaney_a12([10, 11, 12], [1, 2, 3]) - 1.0) < 1e-9
    assert abs(vargha_delaney_a12([1, 2, 3], [1, 2, 3]) - 0.5) < 1e-9
    adj_test = holm_adjust([0.01, 0.04, 0.03, 0.20])
    assert np.allclose(adj_test, [0.04, 0.09, 0.09, 0.20])
    print("Self-check passed: A12 and Holm adjustment match Appendix D.")
