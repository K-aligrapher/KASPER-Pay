import { useEffect, useMemo, useState } from "react";
import { api, api2, ApiError, type Upload, type WorkflowSummary } from "../api";
import { Bunny } from "../Bunny";

type Op = "convert" | "merge" | "images_to_pdf" | "extract" | "rotate" | "compress" | "stamp" | "protect" | "unlock" | "ocr" | "to_images" | "repair" | "metadata";

const OPS: { id: Op; label: string; hint: string }[] = [
  { id: "convert", label: "Convert", hint: "Any format to any format" },
  { id: "merge", label: "Merge PDFs", hint: "Combine PDFs in order" },
  { id: "extract", label: "Pick pages", hint: "Split, extract, delete or reorder pages" },
  { id: "rotate", label: "Rotate", hint: "Turn pages 90°, 180° or 270°" },
  { id: "compress", label: "Compress", hint: "Make PDFs smaller" },
  { id: "images_to_pdf", label: "Images → PDF", hint: "One image per page" },
  { id: "stamp", label: "Watermark / numbers", hint: "Add a watermark and/or page numbers" },
  { id: "ocr", label: "OCR", hint: "Make scanned PDFs or images searchable" },
  { id: "protect", label: "Protect", hint: "Add a password (never stored)" },
  { id: "unlock", label: "Unlock", hint: "Remove a password you know" },
  { id: "to_images", label: "PDF → images", hint: "One PNG/JPG per page" },
  { id: "repair", label: "Repair", hint: "Fix a damaged PDF" },
  { id: "metadata", label: "Title / author", hint: "Edit a PDF's title, author, subject, keywords" },
];

/** Document Studio (PRD §9): local conversions and PDF tools. Results wait in Approvals before saving. */
export function Documents({ go, preload }: { go: (r: string) => void; preload?: string | null }) {
  const [files, setFiles] = useState<Upload[]>([]);
  const [op, setOp] = useState<Op>("convert");
  const [to, setTo] = useState("");
  const [lossy, setLossy] = useState<Record<string, boolean>>({});
  const [pages, setPages] = useState("");
  const [degrees, setDegrees] = useState(90);
  const [quality, setQuality] = useState("medium");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [started, setStarted] = useState<WorkflowSummary | null>(null);
  const [engines, setEngines] = useState<{ name: string; installed: boolean; install: string }[]>([]);
  const [drag, setDrag] = useState(false);
  const [watermark, setWatermark] = useState("");
  const [pageNumbers, setPageNumbers] = useState(false);
  const [password, setPassword] = useState("");
  const [language, setLanguage] = useState("eng");
  const [imageFormat, setImageFormat] = useState("png");
  const [dpi, setDpi] = useState(150);
  const [meta, setMeta] = useState({ title: "", author: "", subject: "", keywords: "" });

  // Files sent from Explorer ("Send to → KASPER") arrive as upload ids in the route.
  useEffect(() => {
    if (!preload) return;
    Promise.all(preload.split(",").map((t) => api2.upload(t).catch(() => null)))
      .then((ups) => setFiles((f) => [...f, ...ups.filter((u): u is Upload => !!u && !f.some((x) => x.upload === u.upload))]));
  }, [preload]);

  useEffect(() => { api.engines().then((e) => setEngines(e.engines)).catch(() => {}); }, []);

  // Targets every selected file can reach.
  const targets = useMemo(() => {
    if (files.length === 0) return [];
    return files.map((f) => f.targets).reduce((a, b) => a.filter((t) => b.includes(t)));
  }, [files]);

  useEffect(() => {
    if (!targets.includes(to)) setTo(targets.includes("pdf") ? "pdf" : targets[0] ?? "");
    const ext = files[0]?.fileName.split(".").pop() ?? "";
    if (ext) api.targets(ext).then((t) => setLossy(Object.fromEntries(t.map((x) => [x.to, x.lossy])))).catch(() => {});
  }, [targets]); // eslint-disable-line react-hooks/exhaustive-deps

  const add = async (list: FileList | null) => {
    if (!list?.length) return;
    setError(null);
    setStarted(null);
    try {
      const uploaded = await api.upload(Array.from(list));
      setFiles((f) => [...f, ...uploaded]);
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Could not add the files.");
    }
  };

  const move = (i: number, d: number) =>
    setFiles((f) => {
      const n = [...f];
      const [x] = n.splice(i, 1);
      n.splice(Math.max(0, Math.min(n.length, i + d)), 0, x);
      return n;
    });

  const start = async () => {
    setBusy(true);
    setError(null);
    try {
      const wf = await api.job({
        operation: op,
        uploads: files.map((f) => f.upload),
        to: op === "convert" ? to : undefined,
        pages: op === "extract" || op === "rotate" ? pages || undefined : undefined,
        degrees: op === "rotate" ? degrees : undefined,
        quality: op === "compress" ? quality : undefined,
        watermark: op === "stamp" ? watermark || undefined : undefined,
        pageNumbers: op === "stamp" ? pageNumbers : undefined,
        password: op === "protect" || op === "unlock" ? password : undefined,
        language: op === "ocr" ? language : undefined,
        format: op === "to_images" ? imageFormat : undefined,
        dpi: op === "to_images" ? dpi : undefined,
        ...(op === "metadata" ? Object.fromEntries(Object.entries(meta).filter(([, v]) => v.trim())) : {}),
      });
      setPassword("");
      setStarted(wf);
      setFiles([]);
    } catch (e) {
      setError(e instanceof ApiError ? [e.message, ...e.details].join(" ") : "Could not start.");
    } finally {
      setBusy(false);
    }
  };

  const allPdf = files.length > 0 && files.every((f) => f.fileName.toLowerCase().endsWith(".pdf"));
  const ready =
    files.length > 0 &&
    ((op === "convert" && to) || (op === "merge" && allPdf && files.length > 1) || (op === "images_to_pdf") ||
      (op === "extract" && allPdf && pages.trim()) || ((op === "rotate" || op === "compress") && allPdf) ||
      (op === "stamp" && allPdf && (watermark.trim() || pageNumbers)) || ((op === "protect" || op === "unlock") && allPdf && password) || op === "ocr" ||
      ((op === "to_images" || op === "repair") && allPdf) || (op === "metadata" && allPdf && Object.values(meta).some((v) => v.trim())));

  return (
    <div className="page">
      <header className="page-head">
        <h1>Documents</h1>
        <p className="muted">Everything runs on this PC. Your originals are never changed, and results wait for your approval before saving.</p>
      </header>

      <div className="op-tabs">
        {OPS.map((o) => (
          <button key={o.id} className={o.id === op ? "op-tab active" : "op-tab"} onClick={() => setOp(o.id)} title={o.hint}>{o.label}</button>
        ))}
      </div>

      <label
        className={drag ? "dropzone drag" : "dropzone"}
        onDragOver={(e) => { e.preventDefault(); setDrag(true); }}
        onDragLeave={() => setDrag(false)}
        onDrop={(e) => { e.preventDefault(); setDrag(false); add(e.dataTransfer.files); }}
      >
        <input type="file" multiple hidden onChange={(e) => { add(e.target.files); e.target.value = ""; }} />
        <Bunny size={48} />
        <span>Drop files here or <u>browse</u></span>
        <span className="muted small">{OPS.find((o) => o.id === op)!.hint}</span>
      </label>

      {files.length > 0 && (
        <ol className="file-list">
          {files.map((f, i) => (
            <li key={f.upload}>
              <code>{f.fileName}</code>
              <span className="muted small">{(f.sizeBytes / 1024).toFixed(0)} KB</span>
              <span className="file-actions">
                <button className="btn-ghost btn-sm" onClick={() => move(i, -1)} disabled={i === 0}>↑</button>
                <button className="btn-ghost btn-sm" onClick={() => move(i, 1)} disabled={i === files.length - 1}>↓</button>
                <button className="btn-ghost btn-sm" onClick={() => setFiles(files.filter((_, j) => j !== i))}>✕</button>
              </span>
            </li>
          ))}
        </ol>
      )}

      {files.length > 0 && (
        <div className="card op-options">
          {op === "convert" && (
            targets.length === 0
              ? <div className="warning">No conversions are available for these files with the engines installed. See below.</div>
              : <label>Convert to{" "}
                  <select value={to} onChange={(e) => setTo(e.target.value)}>
                    {targets.map((t) => <option key={t} value={t}>.{t}</option>)}
                  </select>
                </label>
          )}
          {op === "convert" && lossy[to] && <div className="warning">⚠ This conversion can lose layout or formatting. Check the result before approving.</div>}
          {(op === "extract" || op === "rotate") && (
            <label>{op === "extract" ? "Pages to keep (in order)" : "Pages to rotate (empty = all)"}{" "}
              <input value={pages} placeholder="e.g. 1-3,5" onChange={(e) => setPages(e.target.value)} />
            </label>
          )}
          {op === "rotate" && (
            <label>Degrees{" "}
              <select value={degrees} onChange={(e) => setDegrees(Number(e.target.value))}>
                <option value={90}>90° clockwise</option><option value={180}>180°</option><option value={270}>90° anticlockwise</option>
              </select>
            </label>
          )}
          {op === "compress" && (
            <label>Quality{" "}
              <select value={quality} onChange={(e) => setQuality(e.target.value)}>
                <option value="high">High (print)</option><option value="medium">Medium (email)</option><option value="low">Low (smallest)</option>
              </select>
            </label>
          )}
          {op === "stamp" && (
            <>
              <label>Watermark text <input value={watermark} placeholder="e.g. DRAFT" onChange={(e) => setWatermark(e.target.value)} /></label>
              <label><input type="checkbox" checked={pageNumbers} onChange={(e) => setPageNumbers(e.target.checked)} /> Add page numbers</label>
            </>
          )}
          {(op === "protect" || op === "unlock") && (
            <label>Password <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} />
              <span className="muted small"> kept in memory only, never saved</span></label>
          )}
          {op === "ocr" && (
            <label>Language <input style={{ maxWidth: 120 }} value={language} onChange={(e) => setLanguage(e.target.value)} />
              <span className="muted small"> Tesseract code, e.g. eng, eng+hin</span></label>
          )}
          {op === "to_images" && (
            <>
              <label>Format <select value={imageFormat} onChange={(e) => setImageFormat(e.target.value)}><option value="png">PNG</option><option value="jpg">JPG</option></select></label>
              <label>Quality <select value={dpi} onChange={(e) => setDpi(Number(e.target.value))}><option value={96}>Screen (96 dpi)</option><option value={150}>Normal (150 dpi)</option><option value={300}>Print (300 dpi)</option></select></label>
              <span className="muted small">Several pages come as one .zip.</span>
            </>
          )}
          {op === "metadata" && (["title", "author", "subject", "keywords"] as const).map((k) => (
            <label key={k}>{k[0].toUpperCase() + k.slice(1)} <input value={meta[k]} placeholder="leave empty to keep" onChange={(e) => setMeta({ ...meta, [k]: e.target.value })} /></label>
          ))}
          {(op !== "convert" && op !== "images_to_pdf" && op !== "ocr" && !allPdf) && <div className="warning">This tool works on PDFs. Convert the other files first.</div>}
          {op === "merge" && files.length < 2 && <span className="muted small">Add at least two PDFs.</span>}
          <button className="btn-primary" disabled={!ready || busy} onClick={start}>{busy ? "Starting…" : "Start"}</button>
        </div>
      )}

      {error && <div className="error">{error}</div>}
      {started && (
        <div className="notice">
          Started “{started.summary}”. When it's ready, review it in <a href="#approvals" onClick={(e) => { e.preventDefault(); go("approvals"); }}>Approvals</a> ·{" "}
          <a href={`#history/${started.id}`} onClick={(e) => { e.preventDefault(); go(`history/${started.id}`); }}>details</a>
        </div>
      )}

      <section className="card">
        <h2>Engines</h2>
        <p className="muted small">PDF merge, pages, rotate, compress and images → PDF are built in. Other conversions use these free tools:</p>
        <ul className="plain-list">
          {engines.map((e) => (
            <li key={e.name}>
              <span className={e.installed ? "dot dot--ok" : "dot dot--bad"} /> {e.name}{" "}
              {!e.installed && <code className="muted small">{e.install}</code>}
            </li>
          ))}
        </ul>
      </section>
    </div>
  );
}
