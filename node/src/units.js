/**
 * Opt-in money helpers.
 *
 * **The SDK never calls these for you.** Every monetary field stays exactly the string the API
 * sent, because `Number("2100000000000000000")` silently loses precision — an IEEE-754 double
 * cannot hold a satoshi count above 2^53, let alone a 256-bit wei value. Convert only at the
 * edge where you render, and only with these BigInt-backed functions.
 *
 * `decimals` comes from `GET /api/v1/tickers` (`Ticker.decimals`).
 */

import { ReceivifyError } from './errors.js';

const INTEGER_RE = /^-?[0-9]+$/;
const DECIMAL_RE = /^-?[0-9]*(?:\.[0-9]*)?$/;

/**
 * Exact base units (a string like `"123456"`) -> a display decimal string.
 * Pure string/BigInt arithmetic: no float ever touches the value.
 *
 *   formatUnits('123456', 8) === '0.00123456'
 */
export function formatUnits(base, decimals) {
  const d = assertDecimals(decimals);
  const s = typeof base === 'bigint' ? base.toString() : String(base ?? '').trim();
  if (!INTEGER_RE.test(s)) {
    throw new ReceivifyError(`formatUnits: expected an integer base-unit string, got ${JSON.stringify(base)}`);
  }
  const negative = s.startsWith('-');
  let digits = negative ? s.slice(1) : s;
  if (d === 0) return (negative ? '-' : '') + stripLeadingZeros(digits);

  digits = digits.padStart(d + 1, '0');
  const whole = stripLeadingZeros(digits.slice(0, digits.length - d));
  const frac = digits.slice(digits.length - d);
  return `${negative && (whole !== '0' || /[1-9]/.test(frac)) ? '-' : ''}${whole}.${frac}`;
}

/**
 * A display decimal string -> exact base units as a string.
 * Throws rather than rounding when the input has more fraction digits than the asset has.
 *
 *   parseUnits('0.00123456', 8) === '123456'
 */
export function parseUnits(value, decimals) {
  const d = assertDecimals(decimals);
  const s = String(value ?? '').trim();
  if (s === '' || s === '.' || s === '-' || !DECIMAL_RE.test(s)) {
    throw new ReceivifyError(`parseUnits: expected a decimal string, got ${JSON.stringify(value)}`);
  }
  const negative = s.startsWith('-');
  const body = negative ? s.slice(1) : s;
  const [whole = '', frac = ''] = body.split('.');
  if (frac.length > d) {
    throw new ReceivifyError(
      `parseUnits: ${JSON.stringify(value)} has ${frac.length} fraction digits but the asset has ${d}; ` +
        'refusing to round a monetary value',
    );
  }
  const digits = stripLeadingZeros((whole || '0') + frac.padEnd(d, '0'));
  return digits === '0' ? '0' : (negative ? '-' : '') + digits;
}

/**
 * Compare two exact base-unit strings without going through a Number.
 * Returns -1, 0 or 1.
 */
export function compareAmounts(a, b) {
  const x = toBigInt(a, 'compareAmounts');
  const y = toBigInt(b, 'compareAmounts');
  return x < y ? -1 : x > y ? 1 : 0;
}

/** Exact base-unit string -> BigInt. The safe way to do arithmetic on an amount. */
export function toBigInt(value, who = 'toBigInt') {
  if (typeof value === 'bigint') return value;
  const s = String(value ?? '').trim();
  if (!INTEGER_RE.test(s)) {
    throw new ReceivifyError(`${who}: expected an integer base-unit string, got ${JSON.stringify(value)}`);
  }
  return BigInt(s);
}

function stripLeadingZeros(s) {
  const t = s.replace(/^0+/, '');
  return t === '' ? '0' : t;
}

function assertDecimals(decimals) {
  const d = Number(decimals);
  if (!Number.isInteger(d) || d < 0 || d > 100) {
    throw new ReceivifyError(`decimals must be an integer in 0..100, got ${JSON.stringify(decimals)}`);
  }
  return d;
}
