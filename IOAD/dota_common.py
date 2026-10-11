"""Общие данные и признаки для воспроизводимых экспериментов DotaScience."""
from pathlib import Path

import numpy as np
import pandas as pd

ROOT = Path(__file__).resolve().parent
DATA_PATH = ROOT / "dota-data" / "tb_pro_players_matches.csv"
VALIDATION_START = "2020-08-19"
TEST_START = "2020-11-16"
EXCLUDED = ["match_id", "dt_match", "radiant_win", "isRadiant_avg_r", "isRadiant_avg_d"]


def load_matches():
    df = pd.read_csv(DATA_PATH).sort_values(["dt_match", "match_id"]).reset_index(drop=True)
    assert df.match_id.is_unique
    assert df.radiant_win.notna().all()
    return df


def make_features(df, kind="raw"):
    """Никакие ответы, ID и даты не участвуют в построении признаков."""
    raw = df.drop(columns=EXCLUDED).astype(np.float32)
    if kind == "raw":
        return raw
    names = [c[:-2] for c in raw if c.endswith("_r")]
    blocks = {}
    for name in names:
        r, d = raw[name + "_r"], raw[name + "_d"]
        blocks[name + "_diff"] = r - d
        if not name.startswith("hero_") or name in ["hero_damage_avg", "hero_healing_avg", "hero_kills_avg"]:
            blocks[name + "_mean"] = (r + d) / 2
            blocks[name + "_relative"] = (r - d) / (r.abs() + d.abs() + 1e-6)
    for side in ["r", "d"]:
        blocks["missing_share_" + side] = raw.filter(regex="_" + side + "$", axis=1).isna().mean(axis=1)
        # Частоты — средние количества предыдущих матчей, не точные размеры
        # выборок для каждой вероятности. Это проверяемая эвристика сглаживания.
        freq = raw["freq_" + side]
        blocks["win_shrunk_" + side] = 0.5 + (raw["win_pct_" + side] - 0.5) * freq / (freq + 20)
        blocks["log_freq_" + side] = np.log1p(freq.clip(lower=0))
    blocks["win_shrunk_diff"] = blocks["win_shrunk_r"] - blocks["win_shrunk_d"]
    contrast = pd.DataFrame(blocks, index=raw.index).astype(np.float32)
    if kind == "contrast":
        return contrast
    if kind == "extended":
        return pd.concat([raw, contrast], axis=1)
    if kind == "core":
        return contrast.loc[:, [c for c in contrast if not c.startswith("hero_") or c.startswith(("hero_damage", "hero_healing", "hero_kills"))]]
    raise ValueError("Неизвестный вариант признаков: " + kind)


def partition_masks(df):
    day = df.dt_match.str[:10]
    return day < VALIDATION_START, (day >= VALIDATION_START) & (day < TEST_START), day >= TEST_START
