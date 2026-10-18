# Symbols on external browser pages

The browser image installs the distribution symbol font package, resolving and pinning its candidate version exactly like the other image packages. This supplies monochrome symbols and emoji on external pages without relying on a user's desktop font set.

A product-only SVG change cannot repair external content. Downloading fonts at browser startup was rejected because session roots are read-only and startup must not depend on another network request. A larger desktop font collection would increase the image for unrelated scripts. The image smoke checks actual font fallback for hourglass, duplicate and emoji characters in its running browser; a missing-glyph box must not satisfy the check.

This complements the product's inline SVG icons. It does not claim complete coverage of every script or colour emoji.
