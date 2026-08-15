import { defineConfig } from "vitest/config";

export default defineConfig({
  test: {
    environment: "node",
    include: ["src/**/*.test.ts"],
    // src/ がまだ空なので、テストが1本でも入ったらこの行を消すこと。
    passWithNoTests: true,
    coverage: {
      reporter: ["text", "json", "html"],
      exclude: ["node_modules/", "coverage/", "**/*.config.ts", "**/*.d.ts"],
    },
  },
});
