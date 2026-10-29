import { test } from 'claude-code/testing'
test('throwing prompt.submit hook', async ($, on) => {
  on('prompt.submit', () => { throw new Error('boom') })
  let outcome
  try { outcome = { resolved: await $.prompt.submit({ text: 'x' }) } } catch (error) { outcome = { rejected: String(error && error.message || error) } }
  console.log('PROBE ' + JSON.stringify(outcome))
})
