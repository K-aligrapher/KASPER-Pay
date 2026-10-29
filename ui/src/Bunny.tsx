import gridText from "../../assets/brand/kasper-logo.grid.txt?raw";

// Same palette as assets/brand/render-logo.mjs.
const COLORS: Record<string, string> = { B: "#0D0D0D", G: "#F5B800", D: "#B37F00", W: "#FFF6D6" };

const rows = gridText.split(/\r?\n/).filter((l) => l && !l.startsWith("#"));
const width = rows[0].length;

const rects = rows.flatMap((row, y) =>
  [...row].flatMap((ch, x) => (COLORS[ch] ? [{ x, y, fill: COLORS[ch] }] : []))
);

/** The KASPER pixel bunny. `size` is the rendered width in px; keep it a multiple of 16. */
export function Bunny({ size = 32, thinking = false }: { size?: number; thinking?: boolean }) {
  return (
    <svg
      className={thinking ? "bunny bunny--thinking" : "bunny"}
      viewBox={`0 0 ${width} ${rows.length}`}
      width={size}
      height={(size * rows.length) / width}
      shapeRendering="crispEdges"
      role="img"
      aria-label="KASPER"
    >
      {rects.map((r) => (
        <rect key={`${r.x}-${r.y}`} x={r.x} y={r.y} width={1} height={1} fill={r.fill} />
      ))}
    </svg>
  );
}
