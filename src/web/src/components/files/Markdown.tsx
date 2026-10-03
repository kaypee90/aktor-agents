"use client";

import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";

/** Markdown (with tables, task lists and strikethrough) styled for reading. Raw HTML in the
 * source is not rendered, so a file can't inject markup into the page. */
export function Markdown({ text, compact = false }: { text: string; compact?: boolean }) {
  return (
    <div className={compact ? "space-y-2 text-sm" : "space-y-3 text-[15px] leading-7"}>
      <ReactMarkdown
        remarkPlugins={[remarkGfm]}
        components={{
          h1: ({ children }) => <h1 className="mt-6 border-b border-zinc-200 pb-2 text-2xl font-semibold text-zinc-900 first:mt-0 dark:border-zinc-800 dark:text-zinc-50">{children}</h1>,
          h2: ({ children }) => <h2 className="mt-6 text-xl font-semibold text-zinc-900 first:mt-0 dark:text-zinc-50">{children}</h2>,
          h3: ({ children }) => <h3 className="mt-5 text-lg font-semibold text-zinc-900 dark:text-zinc-100">{children}</h3>,
          h4: ({ children }) => <h4 className="mt-4 font-semibold text-zinc-900 dark:text-zinc-100">{children}</h4>,
          p: ({ children }) => <p className="text-zinc-700 dark:text-zinc-300">{children}</p>,
          a: ({ children, href }) => <a href={href} target="_blank" rel="noopener noreferrer" className="text-brand-600 underline hover:no-underline dark:text-brand-400">{children}</a>,
          ul: ({ children }) => <ul className="list-disc space-y-1 pl-6 text-zinc-700 dark:text-zinc-300">{children}</ul>,
          ol: ({ children }) => <ol className="list-decimal space-y-1 pl-6 text-zinc-700 dark:text-zinc-300">{children}</ol>,
          blockquote: ({ children }) => <blockquote className="border-l-4 border-zinc-300 pl-4 italic text-zinc-600 dark:border-zinc-700 dark:text-zinc-400">{children}</blockquote>,
          hr: () => <hr className="my-6 border-zinc-200 dark:border-zinc-800" />,
          pre: ({ children }) => <pre className="overflow-x-auto rounded-lg bg-zinc-100 p-3 text-[13px] leading-5 dark:bg-zinc-950">{children}</pre>,
          code: ({ children, className }) => className
            ? <code className={`font-mono ${className}`}>{children}</code>
            : <code className="rounded bg-zinc-100 px-1 py-0.5 font-mono text-[0.9em] dark:bg-zinc-800">{children}</code>,
          table: ({ children }) => <div className="overflow-x-auto"><table className="min-w-full border-collapse text-sm">{children}</table></div>,
          th: ({ children }) => <th className="border border-zinc-200 bg-zinc-50 px-3 py-1.5 text-left font-semibold dark:border-zinc-700 dark:bg-zinc-800">{children}</th>,
          td: ({ children }) => <td className="border border-zinc-200 px-3 py-1.5 align-top dark:border-zinc-700">{children}</td>,
          img: ({ src, alt }) => (typeof src === "string" ? <span className="text-xs text-zinc-500">[image: {alt || src}]</span> : null),
        }}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
}
