# The sandbox every study analysis runs in (docs/studies.md): one job per container, no network,
# read-only filesystem except /tmp. Jobs arrive as a tar stream on stdin and results leave as a tar
# stream on stdout, so nothing on the host is mounted.
FROM python:3.12-slim

RUN pip install --no-cache-dir \
      numpy==2.1.3 \
      pandas==2.2.3 \
      pyarrow==18.1.0 \
      scipy==1.14.1 \
      statsmodels==0.14.4 \
      scikit-learn==1.5.2 \
      sympy==1.13.3 \
      duckdb==1.1.3 \
      matplotlib==3.9.2 \
      openpyxl==3.1.5

COPY runner.py /app/runner.py

ENV HOME=/tmp MPLCONFIGDIR=/tmp/mpl MPLBACKEND=Agg PYTHONDONTWRITEBYTECODE=1 PYTHONUNBUFFERED=1
USER 65534:65534
ENTRYPOINT ["python", "/app/runner.py"]
