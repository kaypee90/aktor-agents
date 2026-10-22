"use client";

import { useRouter, useSearchParams } from "next/navigation";
import { Suspense, useState } from "react";
import { useAuth } from "@/components/platform/AuthProvider";
import { DeployDialog } from "@/components/solutions/DeployDialog";
import { SolutionDetail } from "@/components/solutions/SolutionDetail";
import { SolutionList } from "@/components/solutions/SolutionList";
import type { Role } from "@/lib/platformTypes";

/**
 * Solutions (docs/solutions.md): business apps built on the platform and installed as one unit,
 * each with its workflows, its data model and records, the assistant its own customers talk to,
 * and its evals. The list is at /solutions, one install at /solutions?id=…(&tab=…).
 */
export default function SolutionsPage() {
  return <Suspense><Solutions /></Suspense>;
}

function Solutions() {
  const params = useSearchParams();
  const id = params.get("id");
  const { me } = useAuth();
  const role = (me?.role ?? "Viewer") as Role;
  const [deploying, setDeploying] = useState<{ installId?: string; inputs?: Record<string, string>; packageRef?: string } | null>(null);
  const [refresh, setRefresh] = useState(0);
  const router = useRouter();

  return (
    <div>
      {id ? (
        <SolutionDetail key={`${id}-${refresh}`} id={id} role={role} onUpgrade={(s) => setDeploying({ installId: s.install_id, inputs: s.inputs })} />
      ) : (
        <SolutionList key={refresh} role={role} onDeploy={(packageRef, installId) => setDeploying({ packageRef, installId })} />
      )}
      {deploying && (
        <DeployDialog
          installId={deploying.installId}
          packageRef={deploying.packageRef}
          currentInputs={deploying.inputs}
          onClose={() => setDeploying(null)}
          onDone={(installId) => {
            setDeploying(null);
            setRefresh((n) => n + 1);
            // A first install goes straight to connecting it to an app.
            router.push(`/solutions?id=${encodeURIComponent(installId)}${deploying.installId ? "" : "&tab=integrate"}`);
          }}
        />
      )}
    </div>
  );
}
