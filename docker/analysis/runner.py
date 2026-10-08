"""One study analysis job (docs/studies.md).

The job arrives as a tar stream on stdin (job.json plus the data files it names) and the result
leaves as a tar stream on stdout (result.json plus any files the job wrote, such as charts or the
train/holdout split of an ingested dataset). The container has no network and only /tmp is
writable, so a job sees nothing but what it was given.

Ops:
  ingest        read an uploaded file, clean its column names, split off the sealed holdout, profile it
  query         read-only SQL (DuckDB) over the given tables
  fit           linear regression, logistic regression or ARIMA, with diagnostics
  analysis      arbitrary Python over the given tables; open matplotlib figures are saved as charts
  holdout_eval  fit on the training data, score on the holdout
"""

import contextlib
import io
import json
import math
import os
import re
import shutil
import sys
import tarfile
import time
import traceback

IN_DIR, OUT_DIR = "/tmp/in", "/tmp/out"
MAX_STDOUT = 20_000


class UserError(Exception):
    """A problem with the request (unknown column, wrong method...), reported plainly."""


# ---------------------------------------------------------------- io

def read_job():
    os.makedirs(IN_DIR, exist_ok=True)
    os.makedirs(OUT_DIR, exist_ok=True)
    with tarfile.open(fileobj=sys.stdin.buffer, mode="r|") as tar:
        for member in tar:
            if not member.isfile():
                continue
            name = os.path.normpath(member.name)
            if name.startswith("..") or os.path.isabs(name):
                continue
            dest = os.path.join(IN_DIR, name)
            os.makedirs(os.path.dirname(dest), exist_ok=True)
            with tar.extractfile(member) as src, open(dest, "wb") as dst:
                shutil.copyfileobj(src, dst)
    with open(os.path.join(IN_DIR, "job.json"), encoding="utf-8") as f:
        return json.load(f)


def write_result(result):
    with open(os.path.join(OUT_DIR, "result.json"), "w", encoding="utf-8") as f:
        json.dump(clean(result), f, allow_nan=False)
    with tarfile.open(fileobj=sys.stdout.buffer, mode="w|") as tar:
        for name in sorted(os.listdir(OUT_DIR)):
            tar.add(os.path.join(OUT_DIR, name), arcname=name)


def clean(value):
    """JSON-safe: numpy scalars to Python, NaN/inf to null, timestamps to ISO strings."""
    import numpy as np
    import pandas as pd

    if isinstance(value, dict):
        return {str(k): clean(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [clean(v) for v in value]
    if isinstance(value, (np.integer,)):
        return int(value)
    if isinstance(value, (np.floating, float)):
        v = float(value)
        return None if math.isnan(v) or math.isinf(v) else round(v, 10)
    if isinstance(value, (np.bool_,)):
        return bool(value)
    if isinstance(value, (pd.Timestamp,)):
        return None if pd.isna(value) else value.isoformat()
    if isinstance(value, np.ndarray):
        return clean(value.tolist())
    if value is pd.NaT:
        return None
    if isinstance(value, (str, int, bool)) or value is None:
        return value
    return str(value)


def path_in(rel):
    full = os.path.normpath(os.path.join(IN_DIR, rel))
    if not full.startswith(IN_DIR + os.sep):
        raise UserError("Bad path.")
    return full


def load_tables(job):
    import pandas as pd
    return {name: pd.read_parquet(path_in(rel)) for name, rel in (job.get("tables") or {}).items()}


# ---------------------------------------------------------------- ingest

def safe_name(name, taken):
    text = str(name).replace("%", " pct ").replace("#", " num ")
    base = re.sub(r"[^0-9a-zA-Z]+", "_", text).strip("_").lower() or "column"
    if base[0].isdigit():
        base = "c_" + base
    candidate, i = base, 2
    while candidate in taken:
        candidate, i = f"{base}_{i}", i + 1
    taken.add(candidate)
    return candidate


DATE_HINT = re.compile(r"(date|time|month|day|week|period|year|timestamp)", re.I)


def read_any(path):
    import pandas as pd
    ext = os.path.splitext(path)[1].lower()
    if ext in (".csv", ".txt"):
        df = pd.read_csv(path, low_memory=False)
        if df.shape[1] == 1:  # maybe ; or tab separated
            for sep in (";", "\t", "|"):
                alt = pd.read_csv(path, sep=sep, low_memory=False)
                if alt.shape[1] > 1:
                    return alt
        return df
    if ext == ".tsv":
        return pd.read_csv(path, sep="\t", low_memory=False)
    if ext in (".xlsx", ".xlsm"):
        return pd.read_excel(path)
    if ext == ".parquet":
        return pd.read_parquet(path)
    if ext in (".json", ".jsonl", ".ndjson"):
        try:
            return pd.read_json(path, lines=ext != ".json")
        except ValueError:
            return pd.read_json(path, lines=True)
    raise UserError(f"Unsupported file type '{ext}'. Use CSV, TSV, Excel (.xlsx), Parquet or JSON.")


def column_kind(series):
    import pandas as pd
    if pd.api.types.is_bool_dtype(series):
        return "boolean"
    if pd.api.types.is_numeric_dtype(series):
        return "numeric"
    if pd.api.types.is_datetime64_any_dtype(series):
        return "datetime"
    distinct = series.nunique(dropna=True)
    return "categorical" if distinct <= max(20, len(series) * 0.05) else "text"


def profile_column(name, original, series):
    import pandas as pd
    kind = column_kind(series)
    info = {
        "name": name, "original": original, "kind": kind, "dtype": str(series.dtype),
        "missing": int(series.isna().sum()), "distinct": int(series.nunique(dropna=True)),
    }
    if kind == "numeric":
        s = pd.to_numeric(series, errors="coerce")
        info.update(min=s.min(), max=s.max(), mean=s.mean(), std=s.std(), median=s.median())
    elif kind == "datetime":
        info.update(min=series.min(), max=series.max())
    elif kind in ("categorical", "boolean"):
        top = series.value_counts(dropna=True).head(8)
        info["top_values"] = [{"value": str(k), "count": int(v)} for k, v in top.items()]
    return info


def op_ingest(job):
    import numpy as np
    import pandas as pd

    df = read_any(path_in(job["file"]))
    if df.shape[1] == 0 or len(df) == 0:
        raise UserError("The file has no rows or no columns.")

    taken, mapping = set(), {}
    for col in df.columns:
        mapping[col] = safe_name(col, taken)
    df = df.rename(columns=mapping)
    originals = {v: str(k) for k, v in mapping.items()}

    # Text that is really numbers or dates becomes numbers or dates.
    for col in df.columns:
        s = df[col]
        if s.dtype == object:
            as_num = pd.to_numeric(s, errors="coerce")
            if s.notna().sum() and as_num.notna().sum() >= 0.95 * s.notna().sum():
                df[col] = as_num
                continue
            if DATE_HINT.search(col):
                as_date = pd.to_datetime(s, errors="coerce", format="mixed")
                if s.notna().sum() and as_date.notna().sum() >= 0.95 * s.notna().sum():
                    df[col] = as_date

    time_column = job.get("time_column")
    if time_column:
        time_column = mapping.get(time_column, time_column)
        if time_column not in df.columns:
            raise UserError(f"No column '{job.get('time_column')}'. Columns: {', '.join(df.columns)}.")
        if not pd.api.types.is_datetime64_any_dtype(df[time_column]) and not pd.api.types.is_numeric_dtype(df[time_column]):
            df[time_column] = pd.to_datetime(df[time_column], errors="coerce", format="mixed")
        df = df.sort_values(time_column, kind="stable").reset_index(drop=True)

    fraction = min(max(float(job.get("holdout_fraction", 0.2)), 0.0), 0.5)
    n = len(df)
    k = int(round(n * fraction)) if n >= 20 else 0
    if k == 0:
        train, holdout = df, df.iloc[0:0]
    elif time_column:
        train, holdout = df.iloc[: n - k], df.iloc[n - k:]
    else:
        rng = np.random.default_rng(int(job.get("seed", 7)))
        idx = rng.permutation(n)
        holdout, train = df.iloc[np.sort(idx[:k])], df.iloc[np.sort(idx[k:])]

    train.to_parquet(os.path.join(OUT_DIR, "train.parquet"), index=False)
    holdout.to_parquet(os.path.join(OUT_DIR, "holdout.parquet"), index=False)

    notes = []
    if n < 20:
        notes.append("Fewer than 20 rows: no holdout was sealed.")
    return {
        "rows": n,
        "train_rows": len(train),
        "holdout_rows": len(holdout),
        "time_column": time_column,
        "columns": [profile_column(c, originals.get(c, c), df[c]) for c in df.columns],
        "sample": train.head(5).to_dict(orient="records"),
        "notes": notes,
    }


# ---------------------------------------------------------------- query

ALLOWED_SQL = re.compile(r"^\s*(select|with)\b", re.I)


def strip_sql_comments(sql):
    sql = re.sub(r"/\*.*?\*/", " ", sql, flags=re.S)
    return re.sub(r"--[^\n]*", " ", sql)


def connect(tables):
    import duckdb
    con = duckdb.connect(":memory:")
    for name, frame in tables.items():
        con.register(name, frame)
    # Only the registered tables: no reading files or anything else from here on.
    con.execute("SET enable_external_access = false")
    con.execute("SET lock_configuration = true")
    return con


def op_query(job):
    sql = strip_sql_comments(job.get("sql") or "").strip().rstrip(";").strip()
    if not ALLOWED_SQL.match(sql) or ";" in sql:
        raise UserError("Only one read-only query: SELECT ... or WITH ... SELECT ...")
    con = connect(load_tables(job))
    try:
        cur = con.execute(sql)
    except Exception as e:  # duckdb errors are the user's to fix
        raise UserError(str(e).split("\n")[0])
    columns = [d[0] for d in cur.description]
    max_rows = int(job.get("max_rows", 200))
    rows = cur.fetchmany(max_rows + 1)
    return {
        "columns": columns,
        "rows": [list(r) for r in rows[:max_rows]],
        "row_count": min(len(rows), max_rows),
        "truncated": len(rows) > max_rows,
    }


# ---------------------------------------------------------------- models

def require_columns(df, cols):
    missing = [c for c in cols if c not in df.columns]
    if missing:
        raise UserError(f"Unknown column(s): {', '.join(missing)}. Columns: {', '.join(df.columns)}.")


def design(frames, target, features):
    """Design matrices for one or more frames with the same dummy columns (train and holdout)."""
    import pandas as pd
    for f in frames:
        require_columns(f, [target] + features)
    parts = [f[[target] + features].dropna() for f in frames]
    dropped = [len(f) - len(p) for f, p in zip(frames, parts)]
    if any(len(p) == 0 for p in parts):
        raise UserError("No rows left after dropping rows with missing values in the target or features.")
    both = pd.concat(parts, keys=range(len(parts)))
    X = both[features].copy()
    for c in X.columns:
        if pd.api.types.is_bool_dtype(X[c]):
            X[c] = X[c].astype(int)
    categorical = [c for c in X.columns if not pd.api.types.is_numeric_dtype(X[c])]
    X = pd.get_dummies(X, columns=categorical, drop_first=True, dtype=float).astype(float)
    if X.shape[1] == 0:
        raise UserError("No usable features.")
    return [(X.xs(i), both[target].xs(i)) for i in range(len(parts))], dropped, categorical


def binary_target(y, positive=None):
    import pandas as pd
    values = sorted(pd.unique(y.dropna()), key=str)
    if set(values) <= {0, 1, True, False}:
        return y.astype(int), {"0": 0, "1": 1}
    if len(values) != 2:
        raise UserError(f"Logistic regression needs a target with two values; '{y.name}' has {len(values)}.")
    pos = positive if positive is not None else values[-1]
    if str(pos) not in [str(v) for v in values]:
        raise UserError(f"positive_class '{pos}' isn't one of {values}.")
    mapped = (y.astype(str) == str(pos)).astype(int)
    return mapped, {str(v): int(str(v) == str(pos)) for v in values}


def vif(X):
    from statsmodels.stats.outliers_influence import variance_inflation_factor
    import statsmodels.api as sm
    if X.shape[1] < 2:
        return {}
    Xc = sm.add_constant(X, has_constant="add").values
    out = {}
    for i, name in enumerate(X.columns, start=1):
        try:
            out[name] = variance_inflation_factor(Xc, i)
        except Exception:
            out[name] = None
    return out


def coefficient_table(result, odds=False):
    import numpy as np
    ci = result.conf_int()
    rows = []
    for term in result.params.index:
        row = {
            "term": term, "coef": result.params[term], "std_err": result.bse[term],
            "p_value": result.pvalues[term], "ci_low": ci.loc[term, 0], "ci_high": ci.loc[term, 1],
        }
        if odds:
            row.update(odds_ratio=np.exp(result.params[term]), or_ci_low=np.exp(ci.loc[term, 0]), or_ci_high=np.exp(ci.loc[term, 1]))
        rows.append(row)
    return rows


def fit_linear(train, target, features, options):
    import numpy as np
    import statsmodels.api as sm
    from sklearn.model_selection import KFold
    from statsmodels.stats.diagnostic import het_breuschpagan
    from statsmodels.stats.stattools import durbin_watson, jarque_bera

    [(X, y)], dropped, categorical = design([train], target, features)
    import pandas as pd
    y = pd.to_numeric(y, errors="coerce")
    if y.isna().any():
        raise UserError(f"Linear regression needs a numeric target; '{target}' isn't.")
    Xc = sm.add_constant(X, has_constant="add")
    res = sm.OLS(y, Xc).fit()

    cv = None
    if len(y) >= 20:
        errs = []
        for tr, te in KFold(5, shuffle=True, random_state=0).split(Xc):
            m = sm.OLS(y.iloc[tr], Xc.iloc[tr]).fit()
            errs.append(float(np.sqrt(np.mean((y.iloc[te] - m.predict(Xc.iloc[te])) ** 2))))
        cv = {"folds": 5, "rmse_mean": float(np.mean(errs)), "rmse_std": float(np.std(errs))}

    jb_p = jarque_bera(res.resid)[1]
    bp_p = het_breuschpagan(res.resid, res.model.exog)[1] if X.shape[1] >= 1 else None
    vifs = vif(X)
    warnings = []
    if any(v is not None and v > 10 for v in vifs.values()):
        warnings.append("Multicollinearity: some features have VIF above 10, so their coefficients are unstable.")
    if bp_p is not None and bp_p < 0.05:
        warnings.append("Heteroskedastic residuals (Breusch-Pagan p < 0.05): standard errors may be too small.")
    if jb_p < 0.05:
        warnings.append("Residuals are not normal (Jarque-Bera p < 0.05): small-sample p-values are approximate.")
    if len(y) < 10 * X.shape[1]:
        warnings.append(f"Only {len(y)} rows for {X.shape[1]} features: risk of overfitting.")
    return {
        "method": "linear_regression", "n": int(res.nobs), "dropped_rows": dropped[0], "categorical": categorical,
        "coefficients": coefficient_table(res),
        "metrics": {
            "r2": res.rsquared, "adj_r2": res.rsquared_adj, "aic": res.aic, "bic": res.bic,
            "f_pvalue": res.f_pvalue, "rmse": float(np.sqrt(np.mean(res.resid ** 2))),
        },
        "cross_validation": cv,
        "diagnostics": {
            "durbin_watson": durbin_watson(res.resid), "jarque_bera_p": jb_p, "breusch_pagan_p": bp_p,
            "vif": vifs, "condition_number": res.condition_number,
        },
        "warnings": warnings,
    }


def fit_logistic(train, target, features, options):
    import numpy as np
    import statsmodels.api as sm
    from sklearn.metrics import roc_auc_score
    from sklearn.model_selection import StratifiedKFold

    [(X, y_raw)], dropped, categorical = design([train], target, features)
    y, mapping = binary_target(y_raw, options.get("positive_class"))
    Xc = sm.add_constant(X, has_constant="add")
    warnings = []
    try:
        res = sm.Logit(y, Xc).fit(disp=0, maxiter=200)
        coefs = coefficient_table(res, odds=True)
        prob = res.predict(Xc)
        extra = {"pseudo_r2": res.prsquared, "aic": res.aic, "bic": res.bic, "llr_pvalue": res.llr_pvalue}
    except Exception as e:  # perfect separation, singular matrix
        from sklearn.linear_model import LogisticRegression
        warnings.append(f"Maximum likelihood failed ({type(e).__name__}); fitted with mild regularization, no p-values.")
        m = LogisticRegression(C=100.0, max_iter=1000).fit(X, y)
        coefs = [{"term": "const", "coef": m.intercept_[0], "odds_ratio": np.exp(m.intercept_[0])}] + [
            {"term": c, "coef": b, "odds_ratio": np.exp(b)} for c, b in zip(X.columns, m.coef_[0])]
        prob = m.predict_proba(X)[:, 1]
        extra = {}

    auc = roc_auc_score(y, prob) if y.nunique() == 2 else None
    cv = None
    if y.value_counts().min() >= 5:
        from sklearn.linear_model import LogisticRegression
        scores = []
        for tr, te in StratifiedKFold(5, shuffle=True, random_state=0).split(X, y):
            m = LogisticRegression(C=1e6, max_iter=1000).fit(X.iloc[tr], y.iloc[tr])
            scores.append(roc_auc_score(y.iloc[te], m.predict_proba(X.iloc[te])[:, 1]))
        cv = {"folds": 5, "auc_mean": float(np.mean(scores)), "auc_std": float(np.std(scores))}
    balance = float(y.mean())
    if balance < 0.1 or balance > 0.9:
        warnings.append(f"Imbalanced target: {balance:.0%} positive. Accuracy is misleading; use AUC.")
    vifs = vif(X)
    if any(v is not None and v > 10 for v in vifs.values()):
        warnings.append("Multicollinearity: some features have VIF above 10.")
    return {
        "method": "logistic_regression", "n": int(len(y)), "dropped_rows": dropped[0], "categorical": categorical,
        "target_mapping": mapping, "coefficients": coefs,
        "metrics": dict(extra, auc=auc, accuracy=float(((prob >= 0.5).astype(int) == y).mean()), positive_rate=balance),
        "cross_validation": cv, "diagnostics": {"vif": vifs}, "warnings": warnings,
    }


def series_of(df, target, options):
    import pandas as pd
    require_columns(df, [target])
    time_column = options.get("time_column")
    if time_column:
        require_columns(df, [time_column])
        df = df.sort_values(time_column, kind="stable")
    y = pd.to_numeric(df[target], errors="coerce").dropna()
    if len(y) < 8:
        raise UserError(f"ARIMA needs at least 8 observations; '{target}' has {len(y)}.")
    return y.reset_index(drop=True)


def arima_orders(options):
    order = tuple(int(v) for v in (options.get("order") or [1, 1, 1]))
    seasonal = tuple(int(v) for v in (options.get("seasonal_order") or [0, 0, 0, 0]))
    if len(order) != 3 or len(seasonal) != 4 or any(v < 0 for v in order + seasonal) or max(order) > 5:
        raise UserError("order is [p, d, q] (each 0-5); seasonal_order is [P, D, Q, s].")
    return order, seasonal


def fit_arima(train, target, features, options):
    import numpy as np
    from statsmodels.stats.diagnostic import acorr_ljungbox
    from statsmodels.tsa.arima.model import ARIMA
    from statsmodels.tsa.stattools import adfuller

    y = series_of(train, target, options)
    order, seasonal = arima_orders(options)
    horizon = min(max(int(options.get("horizon", 6)), 1), 120)
    res = ARIMA(y.values, order=order, seasonal_order=seasonal).fit()
    fc = res.get_forecast(horizon)
    ci = fc.conf_int(alpha=0.05)
    k = max(1, min(horizon, len(y) // 5))
    back = ARIMA(y.values[:-k], order=order, seasonal_order=seasonal).fit().forecast(k)
    actual = y.values[-k:]
    with np.errstate(divide="ignore", invalid="ignore"):
        mape = float(np.nanmean(np.abs((actual - back) / actual)) * 100) if np.all(actual != 0) else None
    adf_p = adfuller(y.values)[1] if len(y) >= 10 else None
    lags = max(1, min(10, len(y) // 5))
    lb_p = float(acorr_ljungbox(res.resid, lags=[lags], return_df=True)["lb_pvalue"].iloc[0])
    warnings = []
    if adf_p is not None and adf_p > 0.05 and order[1] == 0:
        warnings.append("The series looks non-stationary (ADF p > 0.05) but d = 0: consider differencing.")
    if lb_p < 0.05:
        warnings.append("Residuals are autocorrelated (Ljung-Box p < 0.05): the model misses structure.")
    return {
        "method": "arima", "n": int(len(y)), "order": list(order), "seasonal_order": list(seasonal),
        "coefficients": [{"term": str(n), "coef": v} for n, v in zip(res.param_names, res.params)],
        # The first d (+ D*s) residuals are the differencing's start-up values, not errors.
        "metrics": {"aic": res.aic, "bic": res.bic,
                    "rmse": float(np.sqrt(np.mean(res.resid[order[1] + seasonal[1] * seasonal[3]:] ** 2)))},
        "backtest": {"holdout_steps": k, "rmse": float(np.sqrt(np.mean((actual - back) ** 2))), "mape": mape},
        "forecast": [{"step": i + 1, "mean": m, "ci_low": lo, "ci_high": hi}
                     for i, (m, (lo, hi)) in enumerate(zip(fc.predicted_mean, ci))],
        "diagnostics": {"adf_p": adf_p, "ljung_box_p": lb_p},
        "warnings": warnings,
    }


FITTERS = {"linear_regression": fit_linear, "logistic_regression": fit_logistic, "arima": fit_arima}


def op_fit(job):
    method = job.get("method")
    if method not in FITTERS:
        raise UserError(f"method is one of: {', '.join(FITTERS)}.")
    tables = load_tables(job)
    train = tables[job["table"]]
    return FITTERS[method](train, job["target"], list(job.get("features") or []), job.get("options") or {})


def op_holdout_eval(job):
    import numpy as np
    import pandas as pd
    import statsmodels.api as sm
    from sklearn.metrics import log_loss, roc_auc_score

    method, target = job.get("method"), job["target"]
    features, options = list(job.get("features") or []), job.get("options") or {}
    train = pd.read_parquet(path_in(job["train"]))
    holdout = pd.read_parquet(path_in(job["holdout"]))
    if len(holdout) == 0:
        raise UserError("This dataset has no sealed holdout.")

    if method == "arima":
        from statsmodels.tsa.arima.model import ARIMA
        y = series_of(train, target, options)
        actual = series_of(holdout, target, options).values if len(holdout) >= 8 else pd.to_numeric(holdout[target], errors="coerce").dropna().values
        order, seasonal = arima_orders(options)
        pred = ARIMA(y.values, order=order, seasonal_order=seasonal).fit().forecast(len(actual))
        err = actual - pred
        mape = float(np.mean(np.abs(err / actual)) * 100) if np.all(actual != 0) else None
        return {"method": method, "n": int(len(actual)), "metrics": {
            "rmse": float(np.sqrt(np.mean(err ** 2))), "mae": float(np.mean(np.abs(err))), "mape": mape}}

    [(Xtr, ytr), (Xho, yho)], dropped, _ = design([train, holdout], target, features)
    Xtr, Xho = sm.add_constant(Xtr, has_constant="add"), sm.add_constant(Xho, has_constant="add")
    if method == "linear_regression":
        ytr, yho = pd.to_numeric(ytr), pd.to_numeric(yho)
        pred = sm.OLS(ytr, Xtr).fit().predict(Xho)
        err = yho - pred
        ss_tot = float(((yho - yho.mean()) ** 2).sum())
        return {"method": method, "n": int(len(yho)), "dropped_rows": dropped[1], "metrics": {
            "rmse": float(np.sqrt(np.mean(err ** 2))), "mae": float(np.mean(np.abs(err))),
            "r2": 1 - float((err ** 2).sum()) / ss_tot if ss_tot else None}}
    if method == "logistic_regression":
        ytr, mapping = binary_target(ytr, options.get("positive_class"))
        yho = yho.astype(str).map({k: v for k, v in mapping.items()}) if mapping != {"0": 0, "1": 1} else yho.astype(int)
        try:
            prob = sm.Logit(ytr, Xtr).fit(disp=0, maxiter=200).predict(Xho)
        except Exception:
            from sklearn.linear_model import LogisticRegression
            prob = LogisticRegression(C=100.0, max_iter=1000).fit(Xtr, ytr).predict_proba(Xho)[:, 1]
        prob = np.clip(np.asarray(prob, dtype=float), 1e-9, 1 - 1e-9)
        return {"method": method, "n": int(len(yho)), "dropped_rows": dropped[1], "metrics": {
            "auc": roc_auc_score(yho, prob) if yho.nunique() == 2 else None,
            "accuracy": float(((prob >= 0.5).astype(int) == yho.values).mean()),
            "log_loss": log_loss(yho, prob, labels=[0, 1]), "brier": float(np.mean((prob - yho.values) ** 2))}}
    raise UserError(f"method is one of: {', '.join(FITTERS)}.")


# ---------------------------------------------------------------- analysis

def op_analysis(job):
    import duckdb  # noqa: F401  (offered to the code)
    import matplotlib
    import matplotlib.pyplot as plt
    import numpy as np
    import pandas as pd
    import scipy
    import scipy.stats as stats
    import statsmodels.api as sm
    import statsmodels.formula.api as smf
    import sympy

    matplotlib.use("Agg")
    tables = load_tables(job)
    con = connect(tables)
    namespace = {
        "pd": pd, "np": np, "sm": sm, "smf": smf, "scipy": scipy, "stats": stats, "sympy": sympy, "plt": plt,
        "tables": list(tables), "load": lambda name: tables[name].copy(),
        "sql": lambda q: con.execute(q).df(), "result": None,
    }
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer):
        exec(compile(job.get("code") or "", "<analysis>", "exec"), namespace)  # noqa: S102 (sandboxed)
    charts = []
    for i, num in enumerate(plt.get_fignums()[:10]):
        name = f"chart_{i + 1}.png"
        plt.figure(num).savefig(os.path.join(OUT_DIR, name), dpi=110, bbox_inches="tight")
        charts.append(name)
    plt.close("all")
    output = buffer.getvalue()
    result = namespace.get("result")
    try:
        json.dumps(clean(result), allow_nan=False)
    except (TypeError, ValueError):
        result = str(result)[:5000]
    return {"stdout": output[:MAX_STDOUT], "stdout_truncated": len(output) > MAX_STDOUT, "result": result, "charts": charts}


OPS = {"ingest": op_ingest, "query": op_query, "fit": op_fit, "analysis": op_analysis, "holdout_eval": op_holdout_eval}


def main():
    started = time.time()
    try:
        job = read_job()
        op = OPS.get(job.get("op"))
        if op is None:
            raise UserError(f"Unknown op '{job.get('op')}'.")
        result = dict(op(job), ok=True)
    except UserError as e:
        result = {"ok": False, "error": str(e)}
    except Exception as e:
        tail = traceback.format_exc().strip().splitlines()[-6:]
        result = {"ok": False, "error": f"{type(e).__name__}: {e}", "trace": tail}
    result["duration_ms"] = int((time.time() - started) * 1000)
    write_result(result)


if __name__ == "__main__":
    main()
