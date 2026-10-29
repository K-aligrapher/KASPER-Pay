import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  base: "./",
  // The bunny is read from ../assets/brand (single source of truth).
  server: { fs: { allow: [".."] } },
  build: { outDir: "dist", emptyOutDir: true },
});
