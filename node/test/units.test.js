import test from 'node:test';
import assert from 'node:assert/strict';

import { ReceivifyError, compareAmounts, formatUnits, parseUnits, toBigInt } from '../src/index.js';

test('formatUnits renders satoshis without touching a float', () => {
  assert.equal(formatUnits('123456', 8), '0.00123456');
  assert.equal(formatUnits('100000000', 8), '1.00000000');
  assert.equal(formatUnits('0', 8), '0.00000000');
  assert.equal(formatUnits('1', 8), '0.00000001');
  assert.equal(formatUnits('2100000000000000', 8), '21000000.00000000');
});

test('formatUnits survives an 18-decimal wei value that a double cannot hold', () => {
  const wei = '2100000000000000001';
  // The reason this function exists: the double loses the last digit outright.
  assert.notEqual(String(Number(wei)), wei);
  assert.equal(formatUnits(wei, 18), '2.100000000000000001');
  // uint256 max, rendered exactly.
  assert.equal(
    formatUnits('115792089237316195423570985008687907853269984665640564039457584007913129639935', 18),
    '115792089237316195423570985008687907853269984665640564039457.584007913129639935',
  );
});

test('formatUnits handles zero decimals and negatives', () => {
  assert.equal(formatUnits('42', 0), '42');
  assert.equal(formatUnits('-42', 0), '-42');
  assert.equal(formatUnits('-1', 8), '-0.00000001');
  assert.equal(formatUnits('-100000000', 8), '-1.00000000');
  assert.equal(formatUnits('-0', 8), '0.00000000');
});

test('formatUnits accepts a BigInt as well as a string', () => {
  assert.equal(formatUnits(123456n, 8), '0.00123456');
});

test('formatUnits refuses anything that is not an integer base-unit string', () => {
  for (const bad of ['1.5', '', 'abc', '1e18', null, undefined, {}]) {
    assert.throws(() => formatUnits(bad, 8), ReceivifyError);
  }
});

test('parseUnits is the exact inverse of formatUnits', () => {
  for (const [base, decimals] of [
    ['123456', 8],
    ['1', 18],
    ['0', 8],
    ['2100000000000000000', 18],
    ['42', 0],
  ]) {
    assert.equal(parseUnits(formatUnits(base, decimals), decimals), base);
  }
});

test('parseUnits accepts sloppy but unambiguous decimals', () => {
  assert.equal(parseUnits('1', 8), '100000000');
  assert.equal(parseUnits('0.1', 8), '10000000');
  assert.equal(parseUnits('.5', 8), '50000000');
  assert.equal(parseUnits('1.', 8), '100000000');
  assert.equal(parseUnits('00001.5', 8), '150000000');
  assert.equal(parseUnits('-0.00000001', 8), '-1');
  assert.equal(parseUnits('0.000', 8), '0');
});

test('parseUnits THROWS rather than rounding away precision', () => {
  assert.throws(() => parseUnits('0.123456789', 8), /refusing to round/);
  assert.throws(() => parseUnits('1.5', 0), /refusing to round/);
});

test('parseUnits rejects junk', () => {
  for (const bad of ['', '-', '.', 'abc', '1e8', '1,5', null]) {
    assert.throws(() => parseUnits(bad, 8), ReceivifyError);
  }
});

test('decimals must be a sane integer', () => {
  assert.throws(() => formatUnits('1', -1), ReceivifyError);
  assert.throws(() => formatUnits('1', 1.5), ReceivifyError);
  assert.throws(() => parseUnits('1', 'eight'), ReceivifyError);
});

test('compareAmounts is exact past 2^53, where Number comparison silently ties', () => {
  const a = '9007199254740993'; // 2^53 + 1
  const b = '9007199254740992'; // 2^53
  assert.equal(Number(a) === Number(b), true); // the trap
  assert.equal(compareAmounts(a, b), 1); // the fix
  assert.equal(compareAmounts(b, a), -1);
  assert.equal(compareAmounts(a, a), 0);
});

test('toBigInt gives you safe arithmetic and rejects non-integers', () => {
  assert.equal(toBigInt('100') + toBigInt('23'), 123n);
  assert.equal(toBigInt(5n), 5n);
  assert.throws(() => toBigInt('1.5'), ReceivifyError);
});

test('a full-payment check reads naturally without ever creating a Number', () => {
  const invoice = { amount_expected: '2100000000000000000', amount_received: '2100000000000000001' };
  assert.equal(compareAmounts(invoice.amount_received, invoice.amount_expected) >= 0, true);
  assert.equal(formatUnits(invoice.amount_received, 18), '2.100000000000000001');
});
