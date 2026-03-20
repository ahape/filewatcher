# WebFrontEnd

The FileWatcher dashboard UI. Built with **HTMX**, **Tailwind CSS v4**, and **TypeScript** (bundled via esbuild).

## Build

```bash
npm ci
npm run build
```

Output goes to `wwwroot/` (gitignored). This runs automatically when building the .NET project.

## Scripts

| Script | Description |
|---|---|
| `npm run build` | Full production build (CSS + TS + HTML + vendor) |
| `npm run build:css` | Compile Tailwind CSS to `wwwroot/styles.css` |
| `npm run build:ts` | Bundle TypeScript to `wwwroot/dashboard.js` |
| `npm run build:html` | Copy `index.html` to `wwwroot/` |
| `npm run build:vendor` | Copy `htmx.min.js` to `wwwroot/` |

## Structure

```
src/
  index.html       ← Dashboard page
  input.css        ← Tailwind entry point with @layer components
  dashboard.ts     ← SSE streaming, log rendering, status indicator
wwwroot/           ← Build output (gitignored)
```

## HTMX Resources

- [Documentation](https://htmx.org/docs/) — Start here. Covers attributes, swapping, events, and extensions.
- [Examples](https://htmx.org/examples/) — Practical patterns: infinite scroll, active search, lazy loading, etc.
- [SSE Extension](https://htmx.org/extensions/sse/) — Declarative Server-Sent Events via `hx-ext="sse"`. Useful if you want to replace the imperative `EventSource` code in `dashboard.ts`.
- [Reference](https://htmx.org/reference/) — Full attribute/event/header reference.
- [Hypermedia Systems (free book)](https://hypermedia.systems/) — The philosophy behind HTMX; covers when and why to use hypermedia vs JSON APIs.

## Tailwind CSS v4 Resources

- [Documentation](https://tailwindcss.com/docs) — Utility class reference and configuration.
- [What's new in v4](https://tailwindcss.com/blog/tailwindcss-v4) — Key changes from v3: CSS-first config, `@import "tailwindcss"`, `@source` directives, no `tailwind.config.js` needed.
- [Playground](https://play.tailwindcss.com/) — Live sandbox for experimenting with classes.
- [Cheat Sheet (unofficial)](https://tailwindcomponents.com/cheatsheet/) — Searchable quick reference for all utility classes.

## esbuild Resources

- [Documentation](https://esbuild.github.io/) — Bundler/minifier used for TypeScript compilation.
- [Getting Started](https://esbuild.github.io/getting-started/) — CLI flags and build API.
