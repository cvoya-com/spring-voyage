// Named export satisfies `import/no-anonymous-default-export` — ESLint's
// `import` plugin otherwise warns that an anonymous object literal as a
// default export makes tooling harder to trace. Named `config` also
// makes the file consistent with `next.config.ts` (which exports a named
// constant) and with the repo's root `eslint.config.mjs`.
const config = {
  plugins: {
    "@tailwindcss/postcss": {},
  },
};

export default config;
