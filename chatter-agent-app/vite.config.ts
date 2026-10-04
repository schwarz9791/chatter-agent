import { resolve } from "node:path";
import { defineConfig } from "vite";

export default defineConfig({
  clearScreen: false,
  build: {
    rolldownOptions: {
      input: {
        index: resolve(import.meta.dirname, "index.html"),
        pairing: resolve(import.meta.dirname, "pairing.html"),
      },
    },
  },
  server: {
    port: 1420,
    strictPort: true,
    watch: { ignored: ["**/src-tauri/**"] },
  },
});
