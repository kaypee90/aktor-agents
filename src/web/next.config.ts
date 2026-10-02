import type { NextConfig } from "next";

const nextConfig: NextConfig = {
  output: "standalone",
  // Keep the dev badge clear of the sidebar's account card.
  devIndicators: { position: "bottom-right" },
};

export default nextConfig;
