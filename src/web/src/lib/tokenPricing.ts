/** USD per 1 million tokens. Preserve small cached-input rates instead of rounding to cents. */
export function formatTokenPrice(perMillion: number): string {
  return new Intl.NumberFormat("en-US", {
    style: "currency",
    currency: "USD",
    minimumFractionDigits: 2,
    maximumFractionDigits: 8,
  }).format(perMillion);
}
