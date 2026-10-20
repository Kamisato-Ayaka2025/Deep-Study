# Reader assets

P4 controlled WebView2 reader. All scripts, CSS and fonts are packaged locally.
`reader.js` constructs DOM with textContent; model content is never executable HTML.
Markdown support is intentionally limited to emphasis and inline code; diagram blocks render plain arrow chains.
Math uses KaTeX 0.18.7 (MIT); vendor/provenance.json records verified npm SHA-512 integrity and vendor/LICENSE is included.
WebView2 host enforces an exact asset map, CSP and navigation/download/permission restrictions; see docs/contracts/P4-generation.md.
