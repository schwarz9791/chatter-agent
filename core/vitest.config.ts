import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
    coverage: {
      reporter: ["text", "json", "html"],
      exclude: ["node_modules/", "coverage/", "**/*.config.ts", "**/*.d.ts"],
    },
  },
});
