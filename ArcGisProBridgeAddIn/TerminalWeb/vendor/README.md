# Vendored terminal renderer

These files are unmodified upstream builds, vendored so the dock pane works on
machines with no internet access. ArcGIS Pro is frequently deployed on locked
down or air-gapped networks, so loading them from a CDN at runtime is not an
option.

| File | Package | Version | License |
| ---- | ------- | ------- | ------- |
| `xterm.js` | [`@xterm/xterm`](https://www.npmjs.com/package/@xterm/xterm) | 5.5.0 | MIT |
| `xterm.css` | [`@xterm/xterm`](https://www.npmjs.com/package/@xterm/xterm) | 5.5.0 | MIT |
| `addon-fit.js` | [`@xterm/addon-fit`](https://www.npmjs.com/package/@xterm/addon-fit) | 0.10.0 | MIT |
| `addon-web-links.js` | [`@xterm/addon-web-links`](https://www.npmjs.com/package/@xterm/addon-web-links) | 0.11.0 | MIT |

To refresh them:

```powershell
$base = "https://cdn.jsdelivr.net/npm"
Invoke-WebRequest "$base/@xterm/xterm@5.5.0/lib/xterm.js" -OutFile xterm.js
Invoke-WebRequest "$base/@xterm/xterm@5.5.0/css/xterm.css" -OutFile xterm.css
Invoke-WebRequest "$base/@xterm/addon-fit@0.10.0/lib/addon-fit.js" -OutFile addon-fit.js
Invoke-WebRequest "$base/@xterm/addon-web-links@0.11.0/lib/addon-web-links.js" -OutFile addon-web-links.js
```

The WebGL renderer addon is deliberately not vendored. ArcGIS Pro is already a
heavy GPU consumer, and the default DOM renderer is more than fast enough for a
side pane.
