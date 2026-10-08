"use client";

import { useCallback, useRef, useState } from "react";
import { cx } from "@/components/ui";

const STORE_PREFIX = "aktor:split:";

function readSize(key: string | undefined, fallback: number) {
  if (!key) return fallback;
  try {
    const raw = localStorage.getItem(STORE_PREFIX + key);
    const n = raw === null ? NaN : Number(raw);
    return Number.isFinite(n) ? n : fallback;
  } catch {
    return fallback;
  }
}

function writeSize(key: string | undefined, size: number) {
  if (!key) return;
  try {
    localStorage.setItem(STORE_PREFIX + key, String(Math.round(size)));
  } catch {
    // Storage unavailable: the size just isn't remembered.
  }
}

/**
 * Two panes with a draggable divider. One pane (`sized`: the first or the second) has a size in
 * pixels, kept between `min` and `max` and never more than the container leaves for the other
 * pane's `minOther`; the other takes the rest. Drag the divider, or focus it and use the arrow
 * keys (Shift for bigger steps); double-click resets it. With `storageKey` the size is remembered
 * per viewer. A missing second pane (null) hides it and the divider; the first pane keeps its
 * place in the tree, so showing the second again doesn't remount the first. `collapse` hides
 * either pane (and the divider) while keeping both mounted, so hiding and showing it again keeps
 * its state (a canvas's viewport, an unsent message).
 */
export function Split({
  direction = "horizontal",
  sized = "first",
  initial,
  min = 160,
  max = 2000,
  minOther = 240,
  storageKey,
  className,
  label,
  collapse,
  children,
}: {
  /** "horizontal": side by side; "vertical": stacked. */
  direction?: "horizontal" | "vertical";
  sized?: "first" | "second";
  initial: number;
  min?: number;
  max?: number;
  minOther?: number;
  storageKey?: string;
  className?: string;
  /** What the divider resizes, for screen readers ("Resize the workspace list"). */
  label?: string;
  /** Hides this pane and the divider; the other takes all the space. Both stay mounted. */
  collapse?: "first" | "second" | null;
  children: [React.ReactNode, React.ReactNode | null];
}) {
  const container = useRef<HTMLDivElement>(null);
  const [size, setSize] = useState(() => readSize(storageKey, initial));
  const [dragging, setDragging] = useState(false);
  const horizontal = direction === "horizontal";

  const clamp = useCallback((value: number) => {
    const box = container.current?.getBoundingClientRect();
    const total = box ? (horizontal ? box.width : box.height) : Infinity;
    return Math.max(min, Math.min(max, total - minOther, value));
  }, [horizontal, min, max, minOther]);

  const onPointerDown = (e: React.PointerEvent<HTMLDivElement>) => {
    if (e.button !== 0) return;
    e.preventDefault();
    e.currentTarget.setPointerCapture(e.pointerId);
    setDragging(true);
  };

  const onPointerMove = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragging || !container.current) return;
    const box = container.current.getBoundingClientRect();
    const offset = horizontal ? e.clientX - box.left : e.clientY - box.top;
    const total = horizontal ? box.width : box.height;
    setSize(clamp(sized === "first" ? offset : total - offset));
  };

  const finish = (e: React.PointerEvent<HTMLDivElement>) => {
    if (!dragging) return;
    e.currentTarget.releasePointerCapture(e.pointerId);
    setDragging(false);
    setSize((s) => { writeSize(storageKey, s); return s; });
  };

  const onKeyDown = (e: React.KeyboardEvent<HTMLDivElement>) => {
    const step = e.shiftKey ? 64 : 16;
    const grow = horizontal ? ["ArrowRight"] : ["ArrowDown"];
    const shrink = horizontal ? ["ArrowLeft"] : ["ArrowUp"];
    let delta = grow.includes(e.key) ? step : shrink.includes(e.key) ? -step : 0;
    if (delta === 0) return;
    e.preventDefault();
    if (sized === "second") delta = -delta;
    setSize((s) => { const next = clamp(s + delta); writeSize(storageKey, next); return next; });
  };

  const reset = () => { const next = clamp(initial); setSize(next); writeSize(storageKey, next); };

  const sizedStyle = horizontal ? { width: size } : { height: size };
  const [first, second] = children;
  const missing = second === null || second === undefined || second === false;
  const single = missing || !!collapse;

  return (
    <div ref={container} className={cx("flex h-full min-h-0 w-full min-w-0", horizontal ? "flex-row" : "flex-col", className)}>
      <div className={cx("min-h-0 min-w-0 overflow-hidden", sized === "first" && !single ? "shrink-0" : "flex-1", collapse === "first" && "hidden")}
        style={sized === "first" && !single ? sizedStyle : undefined}>
        {first}
      </div>
      {!missing && <>
      {!single && <div
        role="separator"
        aria-orientation={horizontal ? "vertical" : "horizontal"}
        aria-label={label}
        aria-valuenow={Math.round(size)}
        aria-valuemin={min}
        aria-valuemax={max}
        tabIndex={0}
        title="Drag to resize · double-click to reset"
        onPointerDown={onPointerDown}
        onPointerMove={onPointerMove}
        onPointerUp={finish}
        onPointerCancel={finish}
        onKeyDown={onKeyDown}
        onDoubleClick={reset}
        className={cx(
          "group relative z-10 shrink-0 touch-none select-none outline-none",
          horizontal ? "w-1 cursor-col-resize" : "h-1 cursor-row-resize",
          "bg-zinc-200 transition-colors hover:bg-brand-400 focus-visible:bg-brand-500 dark:bg-zinc-800 dark:hover:bg-brand-500",
          dragging && "bg-brand-500 dark:bg-brand-500",
        )}
      >
        {/* A wider invisible grab area than the visible line. */}
        <span className={cx("absolute", horizontal ? "inset-y-0 -left-1.5 -right-1.5" : "inset-x-0 -top-1.5 -bottom-1.5")} />
      </div>}
      <div className={cx("min-h-0 min-w-0 overflow-hidden", sized === "second" && !single ? "shrink-0" : "flex-1", collapse === "second" && "hidden")}
        style={sized === "second" && !single ? sizedStyle : undefined}>
        {second}
      </div>
      </>}
      {/* While dragging, keep iframes and canvases from swallowing pointer events. */}
      {dragging && <div className={cx("fixed inset-0 z-50", horizontal ? "cursor-col-resize" : "cursor-row-resize")} />}
    </div>
  );
}
