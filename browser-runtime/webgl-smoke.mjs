// Run inside a browser-runtime container against its own Chromium: smoke.ps1 pipes this file
// to `node --input-type=module -` in /opt/browser, where playwright-core is installed.
//
// It checks pixels, not switches. The entrypoint test already pins the switch; what only the
// real image can show is that the distribution's Chromium still ships its software rasteriser
// and can run it as uid 1000 on a read-only root filesystem.
import { chromium } from 'playwright-core';

const browser = await chromium.connectOverCDP(process.env.AGENTHUB_BROWSER_CDP_URL || 'http://127.0.0.1:9223');
let failed = false;
try {
  const page = await browser.contexts()[0].newPage();
  const results = await page.evaluate(() => ['webgl', 'webgl2'].map(kind => {
    const gl = document.createElement('canvas').getContext(kind);
    if (!gl) return { kind, context: false };

    const compile = (type, source) => {
      const shader = gl.createShader(type);
      gl.shaderSource(shader, source);
      gl.compileShader(shader);
      return shader;
    };
    // A clear alone never reaches the shader compiler, which is the part a software
    // rasteriser has to generate machine code for.
    const program = gl.createProgram();
    gl.attachShader(program, compile(gl.VERTEX_SHADER, 'attribute vec2 p; void main() { gl_Position = vec4(p, 0., 1.); }'));
    gl.attachShader(program, compile(gl.FRAGMENT_SHADER, 'void main() { gl_FragColor = vec4(0., 1., 0., 1.); }'));
    gl.linkProgram(program);
    gl.useProgram(program);
    gl.bindBuffer(gl.ARRAY_BUFFER, gl.createBuffer());
    gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 3, -1, -1, 3]), gl.STATIC_DRAW);
    const location = gl.getAttribLocation(program, 'p');
    gl.enableVertexAttribArray(location);
    gl.vertexAttribPointer(location, 2, gl.FLOAT, false, 0, 0);
    gl.drawArrays(gl.TRIANGLES, 0, 3);

    const pixel = new Uint8Array(4);
    gl.readPixels(gl.drawingBufferWidth >> 1, gl.drawingBufferHeight >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, pixel);
    const info = gl.getExtension('WEBGL_debug_renderer_info');
    return {
      kind,
      context: true,
      pixel: Array.from(pixel).join(','),
      renderer: info ? gl.getParameter(info.UNMASKED_RENDERER_WEBGL) : gl.getParameter(gl.RENDERER),
    };
  }));
  await page.close();

  for (const result of results) {
    if (!result.context) {
      console.error(`${result.kind}: getContext returned null`);
      failed = true;
    } else if (result.pixel !== '0,255,0,255') {
      console.error(`${result.kind}: drew ${result.pixel} instead of 0,255,0,255 (${result.renderer})`);
      failed = true;
    } else {
      console.log(`${result.kind}: ok (${result.renderer})`);
    }
  }
} finally {
  await browser.close();
}
process.exit(failed ? 1 : 0);
