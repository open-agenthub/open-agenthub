// Kills only this rig's processes: command line or cwd inside the rig dir; never a shell, vite or this script.
import fs from 'node:fs'
import path from 'node:path'
const R = process.env.RIG_HOME || path.dirname(new URL(import.meta.url).pathname)
for (const pid of fs.readdirSync('/proc').filter(p => /^\d+$/.test(p))) {
  if (Number(pid) === process.pid || pid === '1') continue
  let cmd = '', exe = '', cwd = ''
  try { cmd = fs.readFileSync(`/proc/${pid}/cmdline`, 'utf8').split('\0').join(' '); exe = fs.readlinkSync(`/proc/${pid}/exe`); cwd = fs.readlinkSync(`/proc/${pid}/cwd`) } catch { continue }
  if (!/node|proot|codex|opencode|claude/.test(exe) || /vite|stop\.mjs/.test(cmd)) continue
  if (cmd.includes(R) || cwd.startsWith(R)) { try { process.kill(Number(pid), 'SIGKILL'); console.log('killed', pid, cmd.slice(0, 80)) } catch {} }
}
