'use strict';

// slimRecord: what GET /agenthub/transcript sends for a record longer than one page (#452).
const assert = require('node:assert/strict');
const test = require('node:test');
const path = require('node:path');

const { slimRecord } = require(path.join(__dirname, '..', '..', 'common', 'transcript-records.js'));

test('cuts the image data and keeps the message a person typed beside it', () => {
  const typed = 'Compare this screenshot with the design. ' + 'Details. '.repeat(500);
  const line = JSON.stringify({ type: 'response_item', payload: { type: 'message', role: 'user', content: [
    { type: 'input_text', text: typed }, { type: 'input_image', image_url: 'data:image/png;base64,' + 'A'.repeat(3_000_000) }] } });
  const slim = JSON.parse(slimRecord(line));
  assert.equal(slim.payload.content[0].text, typed);
  assert.ok(slim.payload.content[1].image_url.length < 3000);
  assert.match(slim.payload.content[1].image_url, /characters not shown\]$/);
});

test('cuts the longest strings first and only as many as it takes', () => {
  const line = JSON.stringify({ a: 'x'.repeat(900_000), b: 'y'.repeat(600_000), c: 'z'.repeat(100_000) });
  const slim = JSON.parse(slimRecord(line));
  assert.ok(slim.a.length < 3000);
  assert.equal(slim.b.length, 600_000);
  assert.equal(slim.c.length, 100_000);
});

test('a line that is not a JSON record has nothing to send', () => {
  assert.equal(slimRecord('{"type":"response_item","payload":{"output":"trailing'), null);
  assert.equal(slimRecord('"just a string"'), null);
});

test('a record that stays too large after every long string is cut has nothing to send', () => {
  const many = Array.from({ length: 2000 }, () => 'q'.repeat(2000));
  assert.equal(slimRecord(JSON.stringify({ many })), null);
});
