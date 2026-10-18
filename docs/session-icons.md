# Session icons without font dependencies

Expiry badges in the session header, sidebar and sessions page, and duplicate actions in both lists use inline SVG. The paths inherit the current text colour and occupy one em, so warning colours and compact rows keep their existing layout. Decorative SVGs are hidden from assistive technology; duplicate buttons retain an explicit accessible name.

System glyphs were rejected because a small client font set can render them as empty boxes. A bundled icon font would add a font request and loading dependency for two small shapes. Installing fonts in the integrated browser alone would leave other clients affected; that image improvement is separate from the product icons.
