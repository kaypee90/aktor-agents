"""Generates the sample data for the Studies walkthrough (docs/studies-walkthrough.md).

Run: python3 generate.py   (standard library only; seeded, so it always writes the same files)

The data has known effects built in, so you can check what a study's agents find against the truth:
  - Each 1 point of rent increase lowers the log-odds of renewal by 0.30 (odds ratio about 0.74).
  - Each year of tenure raises them by 0.22; each maintenance request lowers them by 0.18.
  - Riverside tenants renew less (-0.45); two- and three-bedroom tenants more (+0.30).
  - Household income matters a little (+0.012 per $1k above $70k).
  - Occupancy follows a slow upward trend with a summer peak, plus noise.
"""

import csv
import datetime
import math
import random

random.seed(2026)

BUILDINGS = {"Harbor View": 0.0, "Elm Court": 0.1, "Riverside": -0.45}
UNITS = {"studio": (1150, 0.0), "1br": (1450, 0.0), "2br": (1900, 0.3), "3br": (2400, 0.3)}


def renewals(path, n=600):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Lease ID", "Lease End Month", "Building", "Unit Type", "Monthly Rent ($)", "Rent Increase %",
                    "Tenure Years", "Household Income ($k)", "Maintenance Requests (12m)", "Renewed"])
        start = datetime.date(2022, 1, 1)
        for i in range(n):
            building = random.choices(list(BUILDINGS), weights=[0.4, 0.35, 0.25])[0]
            unit = random.choices(list(UNITS), weights=[0.2, 0.4, 0.3, 0.1])[0]
            base_rent, unit_effect = UNITS[unit]
            rent = round(base_rent * random.uniform(0.9, 1.15) / 5) * 5
            increase = round(min(10.0, max(0.0, random.gauss(4.5, 2.6))), 1)
            tenure = round(random.expovariate(1 / 3.2), 1)
            income = round(random.lognormvariate(math.log(72), 0.35))
            maintenance = min(9, int(random.expovariate(1 / 1.6)))
            z = (1.6 - 0.30 * increase + 0.22 * min(tenure, 10) + 0.012 * (income - 70) - 0.18 * maintenance
                 + BUILDINGS[building] + unit_effect)
            renewed = "Yes" if random.random() < 1 / (1 + math.exp(-z)) else "No"
            month = start + datetime.timedelta(days=30.44 * (i * 48 // n))
            # A few households didn't report income: the profile shows them as missing.
            income_cell = "" if random.random() < 0.03 else income
            w.writerow([f"L-{10000 + i}", month.strftime("%Y-%m"), building, unit, rent, increase, tenure,
                        income_cell, maintenance, renewed])


def occupancy(path):
    with open(path, "w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Month", "Occupancy %", "Units Available"])
        for i in range(48):
            year, month = 2022 + i // 12, i % 12 + 1
            value = 91.0 + 0.06 * i + 2.2 * math.sin((month - 4) / 12 * 2 * math.pi) + random.gauss(0, 0.7)
            w.writerow([f"{year}-{month:02d}-01", round(min(99.5, value), 2), 420])


if __name__ == "__main__":
    renewals("lease-renewals.csv")
    occupancy("occupancy.csv")
    print("Wrote lease-renewals.csv and occupancy.csv")
