import { redirect } from "next/navigation";

/** Simulation now lives inside Studies, as experiments (docs/studies.md). */
export default function SimulationPage() {
  redirect("/studies");
}
