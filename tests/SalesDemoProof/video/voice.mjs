// Narration with Kokoro (local, CPU) from narration.json: one WAV per scene, model and revision
// pinned. KokoroTTS.from_pretrained in kokoro-js 1.2.1 drops `revision`, so the model and
// tokenizer are loaded directly with it. Usage (from a directory where kokoro-js is installed):
//   node voice.mjs <narration.json> <outdir>
import fs from 'node:fs'
import { KokoroTTS } from 'kokoro-js'
import { StyleTextToSpeech2Model, AutoTokenizer, RawAudio } from '@huggingface/transformers'

const [file, out] = process.argv.slice(2)
const MODEL = 'onnx-community/Kokoro-82M-v1.0-ONNX'
const revision = '1939ad2a8e416c0acfeecc08a694d14ef25f2231'
const script = JSON.parse(fs.readFileSync(file, 'utf8'))
fs.mkdirSync(out, { recursive: true })
const model = await StyleTextToSpeech2Model.from_pretrained(MODEL, { revision, dtype: 'q8', device: 'cpu',
  session_options: { intraOpNumThreads: 4, interOpNumThreads: 1 } })
const tts = new KokoroTTS(model, await AutoTokenizer.from_pretrained(MODEL, { revision }))
for (const s of script.scenes) {
  // Spoken forms the voice gets wrong as written; the captions keep the written text.
  const spoken = s.text.replace(/AgentHub/g, 'Agent Hub').replace(/\bMCP\b/g, 'M C P').replace(/\bAPI\b/g, 'A P I').replace(/\bTLS\b/g, 'T L S')
  // One call per sentence: a whole paragraph is cut off silently at the model's input limit
  // (the session scene lost its last sentences that way).
  const parts = [], gap = 0.18
  let rate = 24000
  for (const sentence of spoken.match(/[^.!?]+[.!?]+/g) || [spoken]) {
    const a = await tts.generate(sentence.trim(), { voice: script.voice })
    rate = a.sampling_rate
    parts.push(a.audio, new Float32Array(Math.round(gap * rate)))
  }
  const joined = new Float32Array(parts.reduce((n, p) => n + p.length, 0))
  let o = 0; for (const p of parts) { joined.set(p, o); o += p.length }
  await new RawAudio(joined, rate).save(`${out}/${s.id}.wav`)
  console.log(s.id, (joined.length / rate).toFixed(2), 's')
}
