// Edits a demo take into the narrated MP4. Inputs: a take directory from demo/demo.mjs
// (frames/ + timeline.json), the scene WAVs from voice.mjs and narration.json.
//   node edit.mjs <take> <voice-dir> <narration.json> <out-dir>
// Needs ffmpeg/ffprobe on PATH (or FFMPEG/FFPROBE) and the Chromium from demo/browser.mjs for the
// cards and overlays. Writes <out>/demo-481.mp4, captions (.srt/.vtt), edl.json.
//
// Rules the edit keeps, so the film shows what happened:
// - Real footage only. A frame is held until the next one arrives (the screencast sends frames
//   only on repaint); nothing is redrawn or retimed inside an action.
// - Waits for the agent or a pod are played faster and labelled "sped up ×N" on screen.
// - The marked cut (raw /model output, #515) is dropped with a hard cut, no transition.
// - Every app frame carries the stand-in label; every frame says whose browser it is.
import fs from 'node:fs'
import path from 'node:path'
import { execFileSync } from 'node:child_process'
import { chromium } from 'playwright-core'

const [takeDir, voiceDir, narrationFile, outDir] = process.argv.slice(2).map(p => path.resolve(p))
const FFMPEG = process.env.FFMPEG || 'ffmpeg', FFPROBE = process.env.FFPROBE || 'ffprobe'
const FPS = 25, MAX_WAIT = 4, LEAD = 0.5, TAIL = 1.0, CARD_TAIL = 1.5
fs.mkdirSync(`${outDir}/work`, { recursive: true })
const tl = JSON.parse(fs.readFileSync(`${takeDir}/timeline.json`, 'utf8'))
const narration = JSON.parse(fs.readFileSync(narrationFile, 'utf8'))
const wavSeconds = id => Number(execFileSync(FFPROBE, ['-v', 'error', '-show_entries', 'format=duration', '-of', 'csv=p=0', `${voiceDir}/${id}.wav`]).toString())
const run = args => execFileSync(FFMPEG, ['-hide_banner', '-loglevel', 'error', '-y', ...args], { stdio: 'inherit' })

const USERS = {
  alice: 'Alice · admin (ee.admins) · session owner',
  bob: 'Bob · developers group → user · Collaborator',
  visitor: 'Anyone with the link · not signed in · Viewer'
}
const LABEL = 'Synthetic demo data · stand-in model and Git host · Enterprise via test-key build of 1.19.0'

// ---- frame timeline ------------------------------------------------------------------------
const frames = tl.frames
const marks = tl.marks
const scenes = marks.filter(m => m.type === 'scene')
const end = (marks.find(m => m.type === 'end') || marks[marks.length - 1]).t
const ranges = (a, b) => {
  const out = []
  for (const m of marks.filter(x => x.type === a)) {
    const close = marks.find(x => x.type === b && x.t >= m.t && x.name === m.name)
    if (close) out.push([m.t, close.t, m.name])
  }
  return out
}
const waits = ranges('wait-start', 'wait-end'), cuts = ranges('cut-start', 'cut-end')
const frameAt = t => { let lo = 0, hi = frames.length - 1; while (lo < hi) { const mid = (lo + hi + 1) >> 1; if (frames[mid].t <= t) lo = mid; else hi = mid - 1 } return frames[lo] }

// Output frames for one scene: walk source time at 1/FPS per output frame, faster inside waits,
// jumping over cuts. Returns [{file,user,wait}] per output frame.
function sceneFrames(t0, t1) {
  const outFrames = []
  let t = t0
  while (t < t1) {
    const cut = cuts.find(([a, b]) => t >= a && t < b)
    if (cut) { t = cut[1]; continue }
    const w = waits.find(([a, b]) => t >= a && t < b)
    const speed = w ? Math.max(1, (w[1] - w[0]) / MAX_WAIT) : 1
    const f = frameAt(t)
    outFrames.push({ file: f.file, user: f.user, wait: w && speed > 1.05 ? Math.round(speed) : 0 })
    t += speed / FPS
  }
  return outFrames
}

// ---- overlays and cards (rendered in Chromium) ---------------------------------------------------
const browser = await chromium.launch({ executablePath: process.env.CHROME || `${process.env.HOME}/.cache/ms-playwright/chromium-1194/chrome-linux/chrome`, args: ['--no-sandbox', '--disable-dev-shm-usage'] })
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } })
const esc = s => s.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' }[c]))
const FONT = `font-family: Inter, 'DejaVu Sans', Arial, sans-serif;`
async function png(html, file, transparent = true) {
  await page.setContent(`<html><body style="margin:0;width:1920px;height:1080px;${transparent ? 'background:transparent' : 'background:#141413'};${FONT}">${html}</body></html>`)
  await page.screenshot({ path: file, omitBackground: transparent })
  return file
}
const caption = text => `<div style="position:absolute;left:50%;bottom:86px;transform:translateX(-50%);max-width:1500px;padding:14px 26px;border-radius:12px;background:rgba(10,10,10,.82);color:#fff;font-size:34px;line-height:1.3;text-align:center">${esc(text)}</div>`
const badge = user => `<div style="position:absolute;right:28px;top:66px;padding:10px 18px;border-radius:999px;background:rgba(32,96,200,.92);color:#fff;font-size:24px;font-weight:600">${esc(USERS[user] || user)}</div>`
const label = `<div style="position:absolute;left:0;right:0;bottom:0;height:52px;display:flex;align-items:center;justify-content:center;background:#f5b400;color:#111;font-size:24px;font-weight:700;letter-spacing:.2px">${esc(LABEL)}</div>`
const sped = n => `<div style="position:absolute;left:290px;top:66px;padding:10px 18px;border-radius:999px;background:rgba(245,180,0,.95);color:#111;font-size:26px;font-weight:700">⏩ waiting for the agent — sped up ×${n}</div>`

// Sentences of a scene, timed by their share of the words (phrase timing, not forced alignment).
function sentences(text, start, seconds) {
  const parts = text.match(/[^.!?]+[.!?]+(\s|$)/g)?.map(s => s.trim()) || [text]
  const words = parts.map(p => p.split(/\s+/).length), total = words.reduce((a, b) => a + b, 0)
  let t = start
  return parts.map((p, i) => { const d = seconds * words[i] / total; const c = { text: p, start: t, end: t + d }; t += d; return c })
}

// ---- build ---------------------------------------------------------------------------------
const edl = { fps: FPS, scenes: [] }
const captions = []
let clock = 0
const segments = []
const byId = Object.fromEntries(narration.scenes.map(s => [s.id, s]))
const order = narration.scenes.map(s => s.id)
for (const id of order) {
  const s = byId[id], voice = wavSeconds(id), seg = `${outDir}/work/${id}.mp4`
  const cues = sentences(s.text, LEAD, voice)
  if (s.card) {
    const duration = LEAD + voice + CARD_TAIL
    const title = id === 'title'
      ? `<div style="font-size:84px;font-weight:800">Open AgentHub 1.19.0</div><div style="font-size:44px;margin-top:22px;color:#cfcfcf">Issue → pushed branch, with a colleague taking over</div>`
      : `<div style="font-size:60px;font-weight:800">Try it on your own cluster</div><div style="font-size:36px;margin-top:22px;color:#cfcfcf">Quickstart → hostname, TLS and single sign-on → first task.<br>Prospect checklist: steps, measured times, known limits.</div>`
    const card = await png(`<div style="position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;color:#fff;text-align:center">${title}</div>${label}`, `${outDir}/work/${id}-card.png`, false)
    const ov = await Promise.all(cues.map((c, i) => png(caption(c.text), `${outDir}/work/${id}-cap${i}.png`)))
    const filter = ov.map((_, i) => `${i ? `[v${i}]` : '[0]'}[${i + 1}]overlay=enable='between(t,${cues[i].start.toFixed(2)},${cues[i].end.toFixed(2)})'[v${i + 1}]`).join(';')
    run(['-loop', '1', '-framerate', String(FPS), '-t', duration.toFixed(2), '-i', card, ...ov.flatMap(f => ['-i', f]),
      '-filter_complex', filter, '-map', `[v${ov.length}]`, '-c:v', 'libx264', '-pix_fmt', 'yuv420p', '-r', String(FPS), seg])
    segments.push(seg); edl.scenes.push({ id, start: clock, duration, voice, card: true })
    cues.forEach(c => captions.push({ ...c, start: c.start + clock, end: c.end + clock })); clock += duration
    continue
  }
  const sc = scenes.findIndex(m => m.name === id)
  if (sc < 0) throw Error(`scene ${id} missing from the take`)
  let out = sceneFrames(scenes[sc].t, scenes[sc + 1]?.t ?? end)
  const want = Math.ceil((LEAD + voice + TAIL) * FPS)
  while (out.length < want) out.push({ ...out[out.length - 1], wait: 0 }) // hold the last frame for the narration
  const duration = out.length / FPS
  // concat list: runs of the same source frame collapse into one entry.
  let list = 'ffconcat version 1.0\n', i = 0
  while (i < out.length) { let j = i; while (j < out.length && out[j].file === out[i].file) j++; list += `file '${takeDir}/${out[i].file}'\nduration ${((j - i) / FPS).toFixed(4)}\n`; i = j }
  list += `file '${takeDir}/${out[out.length - 1].file}'\n`
  fs.writeFileSync(`${outDir}/work/${id}.ffconcat`, list)
  // Overlay windows: user badge per run of the same user, "sped up" per wait run, captions.
  const windows = []
  const runsOf = key => { const r = []; let a = 0; for (let k = 1; k <= out.length; k++) if (k === out.length || out[k][key] !== out[a][key]) { r.push([a / FPS, k / FPS, out[a][key]]); a = k } return r }
  for (const [a, b, u] of runsOf('user')) windows.push({ a, b, file: await png(badge(u) + label, `${outDir}/work/${id}-u-${u}-${a.toFixed(1)}.png`) })
  for (const [a, b, n] of runsOf('wait')) if (n) windows.push({ a, b, file: await png(sped(n), `${outDir}/work/${id}-w-${a.toFixed(1)}.png`) })
  for (const [k, c] of cues.entries()) windows.push({ a: c.start, b: c.end, file: await png(caption(c.text), `${outDir}/work/${id}-cap${k}.png`) })
  const filter = windows.map((w, k) => `${k ? `[v${k}]` : '[0]'}[${k + 1}]overlay=enable='between(t,${w.a.toFixed(2)},${w.b.toFixed(2)})'[v${k + 1}]`).join(';')
  run(['-f', 'concat', '-safe', '0', '-i', `${outDir}/work/${id}.ffconcat`, ...windows.flatMap(w => ['-i', w.file]),
    '-filter_complex', `[0]scale=1920:1080,fps=${FPS}[base];` + filter.replace('[0]', '[base]'), '-map', `[v${windows.length}]`,
    '-t', duration.toFixed(2), '-c:v', 'libx264', '-preset', 'medium', '-crf', '20', '-pix_fmt', 'yuv420p', '-r', String(FPS), seg])
  const spedUp = runsOf('wait').filter(r => r[2]).map(([a, b, n]) => ({ from: +(clock + a).toFixed(2), to: +(clock + b).toFixed(2), factor: n }))
  segments.push(seg); edl.scenes.push({ id, start: +clock.toFixed(2), duration, voice, spedUp })
  cues.forEach(c => captions.push({ ...c, start: c.start + clock, end: c.end + clock })); clock += duration
}
await browser.close()

// ---- video, audio, mux -------------------------------------------------------------------------
fs.writeFileSync(`${outDir}/work/all.txt`, segments.map(s => `file '${s}'`).join('\n') + '\n')
run(['-f', 'concat', '-safe', '0', '-i', `${outDir}/work/all.txt`, '-c', 'copy', `${outDir}/work/video.mp4`])
const audioInputs = edl.scenes.flatMap(s => ['-i', `${voiceDir}/${s.id}.wav`])
const mix = edl.scenes.map((s, k) => `[${k}]adelay=${Math.round((s.start + LEAD) * 1000)}:all=1[a${k}]`).join(';') +
  ';' + edl.scenes.map((_, k) => `[a${k}]`).join('') + `amix=inputs=${edl.scenes.length}:normalize=0,apad,atrim=0:${clock.toFixed(2)},loudnorm=I=-16:TP=-1.5:LRA=11,aresample=48000[a]`
run([...audioInputs, '-filter_complex', mix, '-map', '[a]', '-c:a', 'pcm_s16le', `${outDir}/work/voice.wav`])
run(['-i', `${outDir}/work/video.mp4`, '-i', `${outDir}/work/voice.wav`, '-map', '0:v', '-map', '1:a', '-c:v', 'copy', '-c:a', 'aac', '-b:a', '160k', '-ar', '48000', '-shortest', '-movflags', '+faststart', `${outDir}/demo-481.mp4`])

const ts = (t, sep) => { const ms = Math.round(t * 1000), h = Math.floor(ms / 3600000), m = Math.floor(ms / 60000) % 60, s = Math.floor(ms / 1000) % 60; return `${String(h).padStart(2, '0')}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}${sep}${String(ms % 1000).padStart(3, '0')}` }
fs.writeFileSync(`${outDir}/demo-481.srt`, captions.map((c, k) => `${k + 1}\n${ts(c.start, ',')} --> ${ts(c.end, ',')}\n${c.text}\n`).join('\n'))
fs.writeFileSync(`${outDir}/demo-481.vtt`, 'WEBVTT\n\n' + captions.map(c => `${ts(c.start, '.')} --> ${ts(c.end, '.')}\n${c.text}\n`).join('\n'))
edl.total = +clock.toFixed(2); edl.facts = tl.facts; edl.captionTiming = 'sentence share of words, not forced alignment'
fs.writeFileSync(`${outDir}/edl.json`, JSON.stringify(edl, null, 1))
console.log(JSON.stringify({ total: edl.total, scenes: edl.scenes.map(s => [s.id, +s.duration.toFixed(1)]) }))
